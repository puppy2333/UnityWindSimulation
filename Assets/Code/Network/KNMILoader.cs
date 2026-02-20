using CesiumForUnity;
using Newtonsoft.Json.Linq;
using System;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Networking;
using System.Collections.Generic;

public class KNMILoader
{
    FluidSimConfig cf;
    RuntimeConfig rcf;

    private const string apiToken = "eyJvcmciOiI1ZTU1NGUxOTI3NGE5NjAwMDEyYTNlYjEiLCJpZCI6ImE0MTQyMTU4MzAwZDQ3MjM4Yjg3OTIyOGYzNWU1YTc1IiwiaCI6Im11cm11cjEyOCJ9";
    private const string apiVersion = "v1";
    private const string collection = "10-minute-in-situ-meteorological-observations";
    private readonly string baseUrl = $"https://api.dataplatform.knmi.nl/edr/{apiVersion}/collections/{collection}";

    CesiumGeoreference cesiumGeoreference;

    double closestDist = double.MaxValue;
    double closestDist2 = double.MaxValue;
    double closestDist3 = double.MaxValue;

    double2 closestPtUnityLoc, closestPt2UnityLoc, closestPt3UnityLoc;
    double2 closestPtLlh, closestPt2Llh, closestPt3Llh;

    double wA, wB, wC;

    int k = 4; // Number of stations to use for IDW
    int p = 2; // Power parameter for IDW

    class Station
    {
        public double2 unityLoc;
        public double2 llhLoc;
        public double dist;

        public Station(double2 unityLocIn, double2 llhLocIn, double distIn)
        {
            unityLoc = unityLocIn;
            llhLoc = llhLocIn;
            dist = distIn;
        }
    }

    List<Station> stations = new List<Station>();

    public KNMILoader(CesiumGeoreference cesiumGeoreferenceIn, FluidSimConfig cfIn, RuntimeConfig rcfIn)
    {
        cesiumGeoreference = cesiumGeoreferenceIn;
        cf = cfIn;
        rcf = rcfIn;
    }

    public async Task<(float, float)> GetWindDirMagIDW()
    {
        // ----- Get all the stations -----
        await GetStations(cf.physFieldPos);

        // ----- Sort stations by distance -----
        stations.Sort((a, b) => {
            return a.dist.CompareTo(b.dist);
        });

        // ----- Interpolate wind data using IDW -----
        (float windDir, float windSpeed) = await InterpWindDirMagIDW();

        return (windDir, windSpeed);
    }

    public async Task<(float, float)> InterpWindDirMagIDW()
    {
        // ----- Use IDW to get wind data -----
        double windVelXNumerator = 0.0;
        double windVelZNumerator = 0.0;
        double denominator = 0.0;

        for (int i = 0; i < k; i++)
        {
            (float stationWindDir, float stationWindSpeed) = await GetPositionData(stations[i].llhLoc);
            Debug.Log($"Station {i} at {stations[i].llhLoc} has wind dir {stationWindDir} and speed {stationWindSpeed}");

            // stationWindVel.x: east-west component (positive east),
            // stationWindVel.y: north-south component (positive north)
            double2 stationWindVel = new(
                stationWindSpeed * math.sin(math.radians(stationWindDir)),
                stationWindSpeed * math.cos(math.radians(stationWindDir))
                );

            //double2 stationWindVel = new(
            //    stationWindSpeed * math.sin(math.radians(stationWindDir - 180)),
            //    stationWindSpeed * math.cos(math.radians(stationWindDir - 180))
            //    );

            double dist = stations[i].dist;

            // Prevent division by zero
            if (dist < 0.0001)
            {
                windVelXNumerator = stationWindVel.x;
                windVelZNumerator = stationWindVel.y;
                denominator = 1.0;
                break;
            }

            double weight = 1.0 / math.pow(dist, 2);

            windVelXNumerator += weight * stationWindVel.x;
            windVelZNumerator += weight * stationWindVel.y;
            denominator += weight;
        }

        double windVelX = windVelXNumerator / denominator;
        double windVelZ = windVelZNumerator / denominator;

        double windSpeed = math.sqrt(math.square(windVelX) + math.square(windVelZ));
        double windDir = math.degrees(math.atan2(windVelX, windVelZ));

        return ((float)windDir, (float)windSpeed);
    }

    public async Task GetStations(double3 queryUnityLoc)
    {
        // ----- Obtain all the coords data -----
        string locationListQuery = $"?datetime=2025-12-02T00:00:00Z";

        string fullUrl = $"{baseUrl}/locations{locationListQuery}";

        using (UnityWebRequest request = UnityWebRequest.Get(fullUrl))
        {
            request.SetRequestHeader("Authorization", apiToken);

            var operation = request.SendWebRequest();

            while (!operation.isDone)
            {
                await Task.Yield();
            }

            if (request.result == UnityWebRequest.Result.Success)
            {
                string jsonString = request.downloadHandler.text;
                JObject json = JObject.Parse(jsonString);
                for (int i = 0; i < 72; i++)
                {
                    // ----- Get station longitude and latitude values -----
                    JToken currLlhJson = json["features"][i]["geometry"]["coordinates"];
                    double3 currLlh = new((double)currLlhJson[0], (double)currLlhJson[1], 0.0);

                    // ----- Convert to Unity location -----
                    double3 currEcef = CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(currLlh);
                    double3 currUnityLoc = cesiumGeoreference.TransformEarthCenteredEarthFixedPositionToUnity(currEcef);

                    // ----- Calculate distance in Unity space -----
                    double dist = math.sqrt(math.square(currUnityLoc.x - queryUnityLoc.x) + math.square(currUnityLoc.z - queryUnityLoc.z));

                    // ----- Store station -----
                    stations.Add(new Station(new(currUnityLoc.x, currUnityLoc.z), new(currLlh.x, currLlh.y), dist));
                }
            }
            else
            {
                Debug.LogError($"{request.error}\nHTTP Code: {request.responseCode}\nResponse: {request.downloadHandler.text}");
            }
        }
    }

    public async Task<(float, float)> GetWindDirMag()
    {
        // ----- Find the 3 closest stations -----
        await GetClosestStations(cf.physFieldPos);

        Debug.Log($"Closest stations at: {closestPtUnityLoc}, {closestPt2UnityLoc}, {closestPt3UnityLoc}");

        // ----- Get data from the 3 closest stations -----
        (float windDirA, float windSpeedA) = await GetPositionData(closestPtLlh);
        (float windDirB, float windSpeedB) = await GetPositionData(closestPt2Llh);
        (float windDirC, float windSpeedC) = await GetPositionData(closestPt3Llh);

        Debug.Log($"Wind dir and speed at three locations: {windDirA} {windSpeedA}, {windDirB} {windSpeedB}, {windDirC} {windSpeedC}");

        // ----- Use barycentric interpolation to get weights -----
        GetInterpolateWeights(new double2(cf.physFieldPos.x, cf.physFieldPos.z));

        Debug.Log($"Interpolation weights: {wA}, {wB}, {wC}");

        (float windDir, float windSpeed) = Interpolate(new float2(windDirA, windSpeedA), new float2(windDirB, windSpeedB), new float2(windDirC, windSpeedC));

        return (windDir, windSpeed);
        //return (0, 1);
    }

    public async Task<(float, float)> GetPositionData(double2 llhCoord)
    {
        //string originCoords = "POINT(5.1797 52.0989)";
        //string originCoords = "POINT(5.1797 52.0989)";
        string originCoords = $"Point({llhCoord.x:F4} {llhCoord.y:F4})";
        Debug.Log($"KNMI request for coords: {originCoords}");

        // ----- Download the 2-3 newest data -----
        DateTime nowUtc = DateTime.UtcNow;
        DateTime queryStopUtc = new DateTime(
            nowUtc.Year, 
            nowUtc.Month, 
            nowUtc.Day, 
            nowUtc.Hour, 
            nowUtc.Minute - (nowUtc.Minute % 10),
            0, 
            0, 
            DateTimeKind.Utc
            );
        DateTime queryStartUtc = queryStopUtc.AddMinutes(-30);

        string queryStartString = queryStartUtc.ToString("yyyy-MM-ddTHH:mm:ssZ");
        string queryStopString = queryStopUtc.ToString("yyyy-MM-ddTHH:mm:ssZ");

        // dd: wind direction, ff: wind speed
        string parameterNames = "dd,ff";

        string query = $"?coords={UnityWebRequest.EscapeURL(originCoords)}" +
                       $"&datetime={queryStartString}/{queryStopString}" +
                       $"&parameter-name={UnityWebRequest.EscapeURL(parameterNames)}";

        string fullUrl = $"{baseUrl}/position{query}";

        using (UnityWebRequest request = UnityWebRequest.Get(fullUrl))
        {
            request.SetRequestHeader("Authorization", apiToken);

            var operation = request.SendWebRequest();

            while (!operation.isDone)
            {
                await Task.Yield();
            }

            if (request.result == UnityWebRequest.Result.Success)
            {
                string jsonString = request.downloadHandler.text;
                JObject json = JObject.Parse(jsonString);
                JToken ddValues = json["coverages"][0]["ranges"]["dd"]["values"];
                float windDir = (float)ddValues.Last;

                JToken ffValues = json["coverages"][0]["ranges"]["ff"]["values"];
                float windSpeed = (float)ffValues.Last;

                return (windDir, windSpeed);
            }
            else
            {
                Debug.LogError($"{request.error}\nHTTP Code: {request.responseCode}\nResponse: {request.downloadHandler.text}");
                return (-1, -1);
            }
        }
    }

    public async Task GetClosestStations(double3 queryUnityLoc)
    {
        // ----- Obtain all the coords data -----
        string locationListQuery = $"?datetime=2025-12-02T00:00:00Z";

        string fullUrl = $"{baseUrl}/locations{locationListQuery}";

        using (UnityWebRequest request = UnityWebRequest.Get(fullUrl))
        {
            request.SetRequestHeader("Authorization", apiToken);

            var operation = request.SendWebRequest();

            while (!operation.isDone)
            {
                await Task.Yield();
            }

            if (request.result == UnityWebRequest.Result.Success)
            {
                string jsonString = request.downloadHandler.text;
                JObject json = JObject.Parse(jsonString);
                for (int i = 0; i < 72; i++)
                {
                    // ----- Get station longitude and latitude values -----
                    JToken currLlhJson = json["features"][i]["geometry"]["coordinates"];
                    double3 currLlh = new((double)currLlhJson[0], (double)currLlhJson[1], 0.0);

                    // ----- Convert to Unity location -----
                    double3 currEcef = CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(currLlh);
                    double3 currUnityLoc = cesiumGeoreference.TransformEarthCenteredEarthFixedPositionToUnity(currEcef);

                    // ----- Calculate distance in Unity space -----
                    double distDiff = math.sqrt(math.square(currUnityLoc.x - queryUnityLoc.x) + math.square(currUnityLoc.z - queryUnityLoc.z));

                    // ----- Update closest points -----
                    if (distDiff < closestDist)
                    {
                        closestDist3 = closestDist2;
                        closestDist2 = closestDist;
                        closestDist = distDiff;

                        closestPt3UnityLoc = closestPt2UnityLoc;
                        closestPt2UnityLoc = closestPtUnityLoc;
                        closestPtUnityLoc = new double2(currUnityLoc.x, currUnityLoc.z);

                        closestPt3Llh = closestPt2Llh;
                        closestPt2Llh = closestPtLlh;
                        closestPtLlh = new double2(currLlh.x, currLlh.y);
                    }
                    else if (distDiff < closestDist2)
                    {
                        closestDist3 = closestDist2;
                        closestDist2 = distDiff;

                        closestPt3UnityLoc = closestPt2UnityLoc;
                        closestPt2UnityLoc = new double2(currUnityLoc.x, currUnityLoc.z);

                        closestPt3Llh = closestPt2Llh;
                        closestPt2Llh = new double2(currLlh.x, currLlh.y);
                    }
                    else if (distDiff < closestDist3)
                    {
                        closestDist3 = distDiff;

                        closestPt3UnityLoc = new double2(currUnityLoc.x, currUnityLoc.z);

                        closestPt3Llh = new double2(currLlh.x, currLlh.y);
                    }
                }
            }
            else
            {
                Debug.LogError($"{request.error}\nHTTP Code: {request.responseCode}\nResponse: {request.downloadHandler.text}");
            }
        }
    }

    public void BarycentricInterpolation(double2 p, double2 a, double2 b, double2 c, double valA, double valB, double valC)
    {
        double det = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);

        wA = ((b.y - c.y) * (p.x - c.x) + (c.x - b.x) * (p.y - c.y)) / det;
        wB = ((c.y - a.y) * (p.x - c.x) + (a.x - c.x) * (p.y - c.y)) / det;
        wC = 1.0f - wA - wB;
    }

    public void GetInterpolateWeights(double2 p)
    {
        BarycentricInterpolation(p, closestPtUnityLoc, closestPt2UnityLoc, closestPt3UnityLoc, closestDist, closestDist2, closestDist3);
    }

    public (float, float) Interpolate(float2 windDirMagA, float2 windDirMagB, float2 windDirMagC)
    {
        float windDir = (float)wA * windDirMagA.x + (float)wB * windDirMagB.x + (float)wC * windDirMagC.x;
        float windMag = (float)wA * windDirMagA.y + (float)wB * windDirMagB.y + (float)wC * windDirMagC.y;

        return (windDir, windMag);
    }
}
