using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;

[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : MonoBehaviour
{
    public float speed = 6f;
    public float gravity = -9.81f;
    public float jumpHeight = 1.5f; // added jump height

    private CharacterController controller;
    private Vector3 velocity;
    private Gun gun;
    private NetworkObject networkObject;

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        gun = GetComponentInChildren<Gun>(true);
        networkObject = GetComponent<NetworkObject>();
    }

    public void OnAttack(InputValue value)
    {
        if (!CanProcessLocalInput()) return;
        if (value == null || !value.isPressed) return;
        FireCurrentGun();
    }

    public void OnAttack()
    {
        if (!CanProcessLocalInput()) return;
        FireCurrentGun();
    }

    private void FireCurrentGun()
    {
        if (gun == null)
            gun = GetComponentInChildren<Gun>(true);

        if (gun == null)
        {
            Debug.LogWarning("PlayerMovement: Attack received but no Gun component was found in children.");
            return;
        }

        gun.TriggerAttack();
    }

    void Update()
    {
        if (!CanProcessLocalInput()) return;

        // New Input System: WASD/Arrows
        Vector2 input = Vector2.zero;
        if (Keyboard.current != null)
        {
            float x = 0f;
            float z = 0f;

            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) x -= 1f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) x += 1f;
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) z += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) z -= 1f;

            input = new Vector2(x, z);
            if (input.sqrMagnitude > 1f) input = input.normalized; // no faster diagonals

            // jump: spacebar
            if (controller.isGrounded && Keyboard.current.spaceKey.wasPressedThisFrame)
            {
                Debug.Log("PlayerMovement: Jump initiated");
                velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }
        }

        Vector3 move = transform.right * input.x + transform.forward * input.y;
        controller.Move(move * speed * Time.deltaTime);

        // gravity
        if (controller.isGrounded && velocity.y < 0f)
            velocity.y = -2f;

        velocity.y += gravity * Time.deltaTime;
        controller.Move(velocity * Time.deltaTime);
    }

    private bool CanProcessLocalInput()
    {
        if (networkObject == null) return true;
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening) return true;
        return networkObject.IsOwner;
    }
}
