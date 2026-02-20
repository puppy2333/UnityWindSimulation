using UnityEngine;

public class MoveObject : MonoBehaviour
{
    // ----- Object move -----
    private Vector3 offset;
    private float zCoord;

    // ----- Object rotate -----
    private Vector3 lastDir;
    private bool isRotating = false;

    void OnMouseDown()
    {
        // ----- Store the distance between object and camera -----
        zCoord = Camera.main.WorldToScreenPoint(gameObject.transform.position).z;

        offset = gameObject.transform.position - GetMouseAsWorldPoint();
    }

    void OnMouseOver()
    {
        float scroll = Input.GetAxis("Mouse ScrollWheel");

        if (scroll != 0)
        {
            Vector3 newScale = transform.localScale + Vector3.one * scroll * 10f;

            if (newScale.x > 0.1f)
            {
                transform.localScale = newScale;
            }
        }

        if (Input.GetMouseButtonDown(1))
        {
            // ----- Store the distance between object and camera -----
            zCoord = Camera.main.WorldToScreenPoint(transform.position).z;

            isRotating = true;

            // ----- Store the initial mouse - obj vector dir -----
            lastDir = GetMouseAsWorldPoint() - transform.position;
            lastDir.y = 0;
        }

        if (Input.GetMouseButton(1) && isRotating)
        {
            // ----- Get the current mouse - obj vector dir -----
            Vector3 currDir = GetMouseAsWorldPoint() - transform.position;
            currDir.y = 0;

            // ----- Rotate -----
            if (currDir.sqrMagnitude > 0.001f && lastDir.sqrMagnitude > 0.001f)
            {
                float angle = Vector3.SignedAngle(lastDir, currDir, Vector3.up);

                transform.Rotate(Vector3.up, angle, Space.World);

                lastDir = currDir;
            }
        }

        if (Input.GetMouseButtonUp(1))
        {
            isRotating = false;
        }

        if (Input.GetKeyDown(KeyCode.Delete))
        {
            Destroy(gameObject);

            Debug.Log("Model deleted!");
        }
    }

    void OnMouseDrag()
    {
        Vector3 newPosition = GetMouseAsWorldPoint() + offset;

        newPosition.y = transform.position.y;

        transform.position = newPosition;
    }

    private Vector3 GetMouseAsWorldPoint()
    {
        Vector3 mousePoint = Input.mousePosition;

        mousePoint.z = zCoord;

        return Camera.main.ScreenToWorldPoint(mousePoint);
    }
}
