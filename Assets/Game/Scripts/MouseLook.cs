using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;

public class MouseLook : NetworkBehaviour
{
    public float mouseSensitivity = 200f;
    public Transform cameraPivot; // assign CameraPivot
    [SerializeField] private float minPitch = -60f;
    [SerializeField] private float maxPitch = 50f;

    float xRotation = 0f;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        if (Mouse.current == null) return;

        Vector2 mouseDelta = Mouse.current.delta.ReadValue();

        float mouseX = mouseDelta.x * mouseSensitivity * Time.deltaTime;
        float mouseY = mouseDelta.y * mouseSensitivity * Time.deltaTime;

        // Yaw on player root
        transform.Rotate(0f, mouseX, 0f);

        // Pitch on camera pivot
        xRotation -= mouseY;
        xRotation = Mathf.Clamp(xRotation, minPitch, maxPitch);
        cameraPivot.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
    }
}
