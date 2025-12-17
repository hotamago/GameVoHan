using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

public class PlayerController : MonoBehaviour {
    [Header("Movement Settings")]
    public float moveSpeed = 5f;
    public float jumpForce = 5f;
    
    [Header("Camera Settings")]
    public float lookSensitivity = 0.5f;
    public float cameraDistance = 5f;
    public float minVerticalAngle = -80f;
    public float maxVerticalAngle = 80f;
    
    private Rigidbody rb;
    private Camera playerCamera;
    
    // Input System actions
    private InputAction moveAction;
    private InputAction lookAction;
    private InputAction jumpAction;
    
    // Camera rotation
    private float horizontalRotation = 0f;
    private float verticalRotation = 0f;
    
    // Movement input
    private Vector2 moveInput;
    private Vector2 lookInput;
    
    private void Start() {
        rb = GetComponent<Rigidbody>();
        playerCamera = Camera.main;
        
        // Tìm và lưu references đến Input Actions
        moveAction = InputSystem.actions.FindAction("Player/Move");
        lookAction = InputSystem.actions.FindAction("Player/Look");
        jumpAction = InputSystem.actions.FindAction("Player/Jump");
        
        // Khởi tạo camera rotation dựa trên hướng hiện tại
        if (playerCamera != null) {
            Vector3 cameraDirection = (playerCamera.transform.position - transform.position).normalized;
            horizontalRotation = Mathf.Atan2(cameraDirection.x, cameraDirection.z) * Mathf.Rad2Deg;
            verticalRotation = Mathf.Asin(cameraDirection.y) * Mathf.Rad2Deg;
        }
        
        // Khóa con trỏ chuột khi bắt đầu
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }
    
    private void Update() {
        // Đọc input từ Input System
        if (moveAction != null) {
            moveInput = moveAction.ReadValue<Vector2>();
        }
        
        if (lookAction != null) {
            lookInput = lookAction.ReadValue<Vector2>();
        }
        
        // Xử lý di chuyển
        HandleMovement();
        
        // Xử lý camera
        HandleCamera();
        
        // Xử lý nhảy
        if (jumpAction != null && jumpAction.WasPressedThisFrame()) {
            Jump();
        }
        
        // Toggle cursor lock với phím Escape
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
    
    private void HandleMovement() {
        // Tính toán hướng di chuyển dựa trên camera (chỉ xoay theo trục Y)
        Vector3 cameraForward = playerCamera.transform.forward;
        Vector3 cameraRight = playerCamera.transform.right;
        
        // Loại bỏ thành phần Y để di chuyển trên mặt phẳng ngang
        cameraForward.y = 0f;
        cameraRight.y = 0f;
        cameraForward.Normalize();
        cameraRight.Normalize();
        
        // Tính toán hướng di chuyển
        Vector3 moveDirection = (cameraForward * moveInput.y + cameraRight * moveInput.x).normalized;
        
        // Áp dụng vận tốc di chuyển
        Vector3 velocity = moveDirection * moveSpeed;
        velocity.y = rb.linearVelocity.y; // Giữ nguyên vận tốc Y (gravity/jump)
        
        rb.linearVelocity = velocity;
    }
    
    private void HandleCamera() {
        if (playerCamera == null || Cursor.lockState != CursorLockMode.Locked) return;
        
        // Cập nhật rotation dựa trên input
        horizontalRotation += lookInput.x * lookSensitivity;
        verticalRotation -= lookInput.y * lookSensitivity;
        
        // Giới hạn góc vertical để tránh camera flip
        verticalRotation = Mathf.Clamp(verticalRotation, minVerticalAngle, maxVerticalAngle);
        
        // Tính toán vị trí camera dựa trên rotation và khoảng cách
        float horizontalRad = horizontalRotation * Mathf.Deg2Rad;
        float verticalRad = verticalRotation * Mathf.Deg2Rad;
        
        // Tính toán offset camera (spherical coordinates)
        float horizontalDistance = cameraDistance * Mathf.Cos(verticalRad);
        Vector3 cameraOffset = new Vector3(
            horizontalDistance * Mathf.Sin(horizontalRad),
            cameraDistance * Mathf.Sin(verticalRad),
            horizontalDistance * Mathf.Cos(horizontalRad)
        );
        
        // Đặt vị trí camera
        playerCamera.transform.position = transform.position + cameraOffset;
        
        // Camera nhìn về phía player
        playerCamera.transform.LookAt(transform.position);
    }
    
    private void Jump() {
        // Kiểm tra xem player có đang đứng trên mặt đất không (có thể cải thiện với raycast)
        if (rb.linearVelocity.y == 0f || Mathf.Abs(rb.linearVelocity.y) < 0.1f) {
            rb.AddForce(Vector3.up * jumpForce, ForceMode.Impulse);
        }
    }
    
    private void OnDisable() {
        // Giải phóng input actions khi script bị disable
        if (moveAction != null) {
            moveAction.Disable();
        }
        if (lookAction != null) {
            lookAction.Disable();
        }
        if (jumpAction != null) {
            jumpAction.Disable();
        }
    }
    
    private void OnEnable() {
        // Kích hoạt input actions khi script được enable
        if (moveAction != null) {
            moveAction.Enable();
        }
        if (lookAction != null) {
            lookAction.Enable();
        }
        if (jumpAction != null) {
            jumpAction.Enable();
        }
    }

    // Draw circle around player
    void OnDrawGizmos() {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, cameraDistance);
    }
}