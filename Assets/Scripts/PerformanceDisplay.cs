using UnityEngine;

public class PerformanceDisplay : MonoBehaviour
{
    [Header("Display Settings")]
    [SerializeField] private bool showFPS = true;
    [SerializeField] private bool showLatency = true;
    [SerializeField] private int fontSize = 24;
    [SerializeField] private Color textColor = Color.white;
    [SerializeField] private Vector2 position = new Vector2(10, 10);
    
    [Header("Update Settings")]
    [SerializeField] private float updateInterval = 0.5f; // Update display every 0.5 seconds
    
    private float deltaTime = 0.0f;
    private float fps = 0.0f;
    private float frameLatency = 0.0f;
    private float lastUpdateTime = 0.0f;
    
    private GUIStyle style;
    private Rect fpsRect;
    private Rect latencyRect;
    
    private void Start()
    {
        // Initialize GUI style
        style = new GUIStyle();
        style.alignment = TextAnchor.UpperLeft;
        style.normal.textColor = textColor;
        style.fontSize = fontSize;
        
        // Calculate positions
        fpsRect = new Rect(position.x, position.y, Screen.width, Screen.height);
        latencyRect = new Rect(position.x, position.y + fontSize + 5, Screen.width, Screen.height);
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
    }
    
    private void OnGUI()
    {
        // Update style color in case it changed
        style.normal.textColor = textColor;
        
        // Display FPS
        if (showFPS)
        {
            string fpsText = $"FPS: {fps:F1}";
            GUI.Label(fpsRect, fpsText, style);
        }
        
        // Display Latency (frame time in ms)
        if (showLatency)
        {
            string latencyText = $"Latency: {frameLatency:F2} ms";
            GUI.Label(latencyRect, latencyText, style);
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

