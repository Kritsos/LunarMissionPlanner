
using UnityEngine;

public class FreeCameraController : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 20f;
    public float fastSpeed = 100f;

    [Header("Mouse Look")]
    public float mouseSensitivity = 2f;

    private float rotationX = 0f;
    private float rotationY = 0f;

    /// <summary>Reseed mouse-look angles after another camera mode changes its pose.</summary>
    public void SynchronizeLookAnglesToTransform()
    {
        Vector3 forward = transform.forward.normalized;
        rotationX = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        rotationY = -Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
    }

    void Update()
    {
        // Mouse look
        if (Input.GetMouseButtonDown(1))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        if (Input.GetMouseButtonUp(1))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        if (Input.GetMouseButton(1))
        {
            rotationX += Input.GetAxis("Mouse X") * mouseSensitivity;
            rotationY -= Input.GetAxis("Mouse Y") * mouseSensitivity;

            rotationY = Mathf.Clamp(rotationY, -89f, 89f);

            transform.rotation = Quaternion.Euler(
                rotationY, rotationX, 0f
            );
        }

        // WASD movement
        float speed = Input.GetKey(KeyCode.LeftShift)
            ? fastSpeed : moveSpeed;

        float horizontal = Input.GetAxisRaw("Horizontal");
        float vertical = Input.GetAxisRaw("Vertical");

        Vector3 movement =
            transform.right * horizontal +
            transform.forward * vertical;

        if (Input.GetKey(KeyCode.E))
            movement += Vector3.up;

        if (Input.GetKey(KeyCode.Q))
            movement += Vector3.down;

        transform.position += movement.normalized *
                              speed * Time.deltaTime;
    }
}
