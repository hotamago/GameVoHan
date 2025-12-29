using UnityEngine;

public class PerformanceDisplay : MonoBehaviour
{
    [Header("Display Settings")]
    [SerializeField] private bool showFPS = true;
    [SerializeField] private bool showLatency = true;
    [SerializeField] private bool showCurrentChunk = true;
    [SerializeField] private bool showPlayerPosition = true;
    [SerializeField] private int fontSize = 24;
    [SerializeField] private Color textColor = Color.white;
    [SerializeField] private Vector2 position = new Vector2(10, 10);
    
    [Header("Update Settings")]
    [SerializeField] private float updateInterval = 0.5f; // Update display every 0.5 seconds
    
    private float deltaTime = 0.0f;
    private float fps = 0.0f;
    private float frameLatency = 0.0f;
    private float lastUpdateTime = 0.0f;
    
    [Header("References")]
    [SerializeField] private Transform player;
    
    private GUIStyle style;
    private Rect fpsRect;
    private Rect latencyRect;
    private Rect currentChunkRect;
    private Rect playerPositionRect;
    
    // Chunk coordinates
    private long current_x = 0;
    private long current_y = 0;
    
    private void Start()
    {
        // Initialize GUI style
        style = new GUIStyle();
        style.alignment = TextAnchor.UpperLeft;
        style.normal.textColor = textColor;
        style.fontSize = fontSize;
        
        // Find player if not assigned
        if (player == null)
        {
            GameObject playerObj = GameObject.FindGameObjectWithTag("Player");
            if (playerObj != null)
            {
                player = playerObj.transform;
            }
        }
        
        // Calculate positions
        float yOffset = 0;
        fpsRect = new Rect(position.x, position.y + yOffset, Screen.width, Screen.height);
        yOffset += fontSize + 5;
        latencyRect = new Rect(position.x, position.y + yOffset, Screen.width, Screen.height);
        yOffset += fontSize + 5;
        currentChunkRect = new Rect(position.x, position.y + yOffset, Screen.width, Screen.height);
        yOffset += fontSize + 5;
        playerPositionRect = new Rect(position.x, position.y + yOffset, Screen.width, Screen.height);
    }
    
    private void Update()
    {
        // Calculate frame time and latency
        deltaTime += (Time.unscaledDeltaTime - deltaTime) * 0.1f;
        frameLatency = Time.unscaledDeltaTime * 1000f; // Convert to milliseconds
        
        // Update FPS display at intervals
        if (Time.unscaledTime - lastUpdateTime >= updateInterval)
        {
            fps = 1.0f / deltaTime;
            lastUpdateTime = Time.unscaledTime;
        }
        
        // Update chunk coordinates
        UpdateChunkCoordinates();
    }
    
    private void UpdateChunkCoordinates()
    {
        // Try to find InfinityRenderChunks or InfinityRenderTerrainChunksBuiltIn
        var renderChunks = FindFirstObjectByType<InfinityTerrain.InfinityRenderChunks>();
        if (renderChunks != null)
        {
            current_x = renderChunks.current_x;
            current_y = renderChunks.current_y;
            return;
        }
        
        var renderTerrainChunks = FindFirstObjectByType<InfinityTerrain.InfinityRenderTerrainChunksBuiltIn>();
        if (renderTerrainChunks != null)
        {
            current_x = renderTerrainChunks.current_x;
            current_y = renderTerrainChunks.current_y;
        }
    }
    
    private void OnGUI()
    {
        // Update style color in case it changed
        style.normal.textColor = textColor;
        
        float yOffset = 0;
        
        // Display FPS
        if (showFPS)
        {
            string fpsText = $"FPS: {fps:F1}";
            fpsRect.y = position.y + yOffset;
            GUI.Label(fpsRect, fpsText, style);
            yOffset += fontSize + 5;
        }
        
        // Display Latency (frame time in ms)
        if (showLatency)
        {
            string latencyText = $"Latency: {frameLatency:F2} ms";
            latencyRect.y = position.y + yOffset;
            GUI.Label(latencyRect, latencyText, style);
            yOffset += fontSize + 5;
        }
        
        // Display Current Chunk Coordinates
        if (showCurrentChunk)
        {
            string chunkText = $"Chunk: ({current_x}, {current_y})";
            currentChunkRect.y = position.y + yOffset;
            GUI.Label(currentChunkRect, chunkText, style);
            yOffset += fontSize + 5;
        }
        
        // Display Player Position
        if (showPlayerPosition && player != null)
        {
            Vector3 pos = player.position;
            string playerText = $"Player: ({pos.x:F2}, {pos.y:F2}, {pos.z:F2})";
            playerPositionRect.y = position.y + yOffset;
            GUI.Label(playerPositionRect, playerText, style);
        }
    }
    
    // Public methods to toggle display
    public void ToggleFPS()
    {
        showFPS = !showFPS;
    }
    
    public void ToggleLatency()
    {
        showLatency = !showLatency;
    }
    
    // Get current performance values
    public float GetFPS()
    {
        return fps;
    }
    
    public float GetLatency()
    {
        return frameLatency;
    }
}

