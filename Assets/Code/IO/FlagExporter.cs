using UnityEngine;
using System.IO;

public class FlagExporter
{
    string filePath;

    public FlagExporter(string filePath)
    {
        this.filePath = filePath;
    }

    public void ExportFlags(int[] flags)
    {
        Debug.Log("flag save path: " + filePath);

        System.Text.StringBuilder sb = new System.Text.StringBuilder();

        for (int i = 0; i < flags.Length; i++)
        {
            if (flags[i] != 0)
            {
                sb.AppendLine(i.ToString());
            }
        }

        string directory = Path.GetDirectoryName(filePath);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(filePath, sb.ToString());
    }
}
