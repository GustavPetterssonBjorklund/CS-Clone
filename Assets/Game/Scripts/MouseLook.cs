using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;

[DisallowMultipleComponent]
public class MouseLook : NetworkBehaviour
{
    private const bool LockCursorOnSpawn = true;

    [SerializeField] private float mouseSensitivity = 200f;
    [SerializeField] private Transform cameraPivot;
    [SerializeField] private float minPitch = -60f;
    [SerializeField] private float maxPitch = 50f;

    private float xRotation;

    private void Awake()
    {
        if (cameraPivot == null)
        {
            cameraPivot = transform.Find("CameraPivot");
        }
    }

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        if (cameraPivot == null)
        {
            Debug.LogWarning("MouseLook: cameraPivot is not assigned.");
            enabled = false;
            return;
        }

        if (LockCursorOnSpawn)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    private void Update()
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
