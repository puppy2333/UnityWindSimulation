using UnityEngine;

public class FreeCameraMovement : MonoBehaviour
{
    public float moveSpeed = 20.0f;

    public float rotationSpeed = 2.0f;

    private float yaw = 0.0f;
    private float pitch = 90.0f;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        //Cursor.visible = false;
    }

    // Update is called once per frame
    void Update()
    {
        if (Cursor.lockState == CursorLockMode.Locked)
        {
            yaw += rotationSpeed * Input.GetAxis("Mouse X");
            pitch -= rotationSpeed * Input.GetAxis("Mouse Y");

            // Limit pitch to prevent camera flipping
            pitch = Mathf.Clamp(pitch, -90f, 90f);

            // Apply rotation.
            transform.eulerAngles = new Vector3(pitch, yaw, 0.0f);
        }

        // Move forward
        float moveForward = Input.GetAxis("Vertical") * moveSpeed * Time.deltaTime;
        // Move right        
        float moveRight = Input.GetAxis("Horizontal") * moveSpeed * Time.deltaTime;

        transform.Translate(Vector3.forward * moveForward);
        transform.Translate(Vector3.right * moveRight);

        // Accelerate movement when holding Shift
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
        {
            transform.Translate(Vector3.forward * moveForward * 2);
            transform.Translate(Vector3.right * moveRight * 2);
        }

        // Move up and down with Q and E keys
        if (Input.GetKey(KeyCode.Q))
        {
            transform.Translate(Vector3.down * moveSpeed * Time.deltaTime);
        }
        if (Input.GetKey(KeyCode.E))
        {
            transform.Translate(Vector3.up * moveSpeed * Time.deltaTime);
        }

        // Unlock cursor when Escape is pressed
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        if (Input.GetKey(KeyCode.L))
        {
            Cursor.lockState = CursorLockMode.Locked;
            //Cursor.visible = false;
        }

        if (Input.GetKey(KeyCode.R))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
