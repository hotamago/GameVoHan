using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

public class FirstPersonControl : MonoBehaviour {
    [Header("Movement Settings")]
    public float walkSpeed = 5f;
    public float runSpeed = 10f;
    public float jumpForce = 5f;
    public float gravity = -9.81f;

    [Header("Camera Settings")]
    public float mouseSensitivity = 2f;
    public float minVerticalAngle = -80f;
    public float maxVerticalAngle = 80f;
    public float defaultFOV = 60f;
    public float zoomFOV = 30f;
    public float zoomSpeed = 5f;

    [Header("Ground Check")]
    public float groundCheckDistance = 0.1f;
    public LayerMask groundLayer;

    private Rigidbody rb;
    private Camera playerCamera;

    // Input System actions
    private InputAction moveAction;
    private InputAction lookAction;
    private InputAction jumpAction;
    private InputAction sprintAction;

    // Camera rotation
    private float verticalRotation = 0f;
    private float horizontalRotation = 0f;

    // Movement input
    private Vector2 moveInput;
    private Vector2 lookInput;
    private bool isSprinting = false;
    private bool isGrounded = true;

    // Zoom
    private float currentFOV;
    private bool isZoomed = false;

    private void Awake() {
        rb = GetComponent<Rigidbody>();
        playerCamera = GetComponentInChildren<Camera>();

        if (playerCamera == null) {
            Debug.LogError("No camera found as child of FirstPersonControl object!");
            return;
        }

        // Set camera to first-person position (at player position)
        playerCamera.transform.localPosition = Vector3.zero;
        playerCamera.transform.localRotation = Quaternion.identity;

        currentFOV = defaultFOV;
        playerCamera.fieldOfView = currentFOV;

        // Initialize input actions
        moveAction = InputSystem.actions.FindAction("Player/Move");
        lookAction = InputSystem.actions.FindAction("Player/Look");
        jumpAction = InputSystem.actions.FindAction("Player/Jump");
        sprintAction = InputSystem.actions.FindAction("Player/Sprint");
    }

    private void Start() {
        // Lock cursor
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void Update() {
        if (playerCamera == null) return;

        // Read input
        if (moveAction != null) {
            moveInput = moveAction.ReadValue<Vector2>();
        }

        if (lookAction != null) {
            lookInput = lookAction.ReadValue<Vector2>();
        }

        if (sprintAction != null) {
            isSprinting = sprintAction.IsPressed();
        }

        // Handle camera look
        HandleCameraLook();

        // Handle zoom
        HandleZoom();

        // Handle jump
        if (jumpAction != null && jumpAction.WasPressedThisFrame() && isGrounded) {
            Jump();
        }

        // Toggle cursor lock with Escape
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) {
            if (Cursor.lockState == CursorLockMode.Locked) {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            } else {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }
    }

    private void FixedUpdate() {
        if (playerCamera == null) return;

        // Check if grounded
        CheckGrounded();

        // Handle movement
        HandleMovement();
    }

    private void HandleMovement() {
        // Calculate movement direction relative to camera
        Vector3 forward = playerCamera.transform.forward;
        Vector3 right = playerCamera.transform.right;

        // Remove Y component to keep movement on horizontal plane
        forward.y = 0f;
        right.y = 0f;
        forward.Normalize();
        right.Normalize();

        // Calculate movement vector
        Vector3 moveDirection = (forward * moveInput.y + right * moveInput.x).normalized;

        // Apply speed based on sprinting
        float currentSpeed = isSprinting ? runSpeed : walkSpeed;

        // Apply movement
        Vector3 velocity = moveDirection * currentSpeed;
        velocity.y = rb.linearVelocity.y; // Preserve vertical velocity for gravity/jumping

        rb.linearVelocity = velocity;
    }

    private void HandleCameraLook() {
        if (Cursor.lockState != CursorLockMode.Locked) return;

        // Update rotations based on mouse input
        horizontalRotation += lookInput.x * mouseSensitivity;
        verticalRotation -= lookInput.y * mouseSensitivity;

        // Clamp vertical rotation
        verticalRotation = Mathf.Clamp(verticalRotation, minVerticalAngle, maxVerticalAngle);

        // Apply rotations
        transform.rotation = Quaternion.Euler(0f, horizontalRotation, 0f);
        playerCamera.transform.localRotation = Quaternion.Euler(verticalRotation, 0f, 0f);
    }

    private void HandleZoom() {
        // Check for mouse wheel input using Input System
        float scrollInput = 0f;
        if (Mouse.current != null) {
            scrollInput = Mouse.current.scroll.ReadValue().y;
        }

        if (scrollInput > 0) {
            isZoomed = true;
        } else if (scrollInput < 0) {
            isZoomed = false;
        }

        // Smoothly interpolate FOV
        float targetFOV = isZoomed ? zoomFOV : defaultFOV;
        currentFOV = Mathf.Lerp(currentFOV, targetFOV, Time.deltaTime * zoomSpeed);
        playerCamera.fieldOfView = currentFOV;
    }

    private void CheckGrounded() {
        // Simple ground check using raycast
        RaycastHit hit;
        isGrounded = Physics.Raycast(transform.position, Vector3.down, out hit, groundCheckDistance, groundLayer);
    }

    private void Jump() {
        rb.AddForce(Vector3.up * jumpForce, ForceMode.Impulse);
        isGrounded = false;
    }

    private void OnDisable() {
        // Clean up input actions
        if (moveAction != null) moveAction.Disable();
        if (lookAction != null) lookAction.Disable();
        if (jumpAction != null) jumpAction.Disable();
        if (sprintAction != null) sprintAction.Disable();
    }

    private void OnEnable() {
        // Enable input actions
        if (moveAction != null) moveAction.Enable();
        if (lookAction != null) lookAction.Enable();
        if (jumpAction != null) jumpAction.Enable();
        if (sprintAction != null) sprintAction.Enable();
    }

    // Draw ground check ray in scene view
    private void OnDrawGizmosSelected() {
        Gizmos.color = isGrounded ? Color.green : Color.red;
        Gizmos.DrawRay(transform.position, Vector3.down * groundCheckDistance);
    }
}
