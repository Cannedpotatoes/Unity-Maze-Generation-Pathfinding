using UnityEngine;

public class FreeFlyCamera : MonoBehaviour
{
    [Header("Movement Settings")]
    [SerializeField] private float moveSpeed = 10f;
    [SerializeField] private float sprintMultiplier = 2f;
    [SerializeField] private float climbSpeed = 8f;

    [Header("View Settings")]
    [SerializeField] private float mouseSensitivity = 2f;
    [SerializeField] private float smoothTime = 0.1f;

    [Header("Maze Reference")]
    [SerializeField] private MazePlugin mazePlugin; 
    [SerializeField] private PathfindingVisualizer pathVisualizer;

    private float currentSpeed = 0f;
    private Vector3 currentVelocity = Vector3.zero;
    private Vector3 smoothMoveVelocity = Vector3.zero;
    
    private float rotationX = 0f;
    private float rotationY = 0f;
    
    private bool isCursorLocked = true;
    private Camera playerCamera;

    private float keyPressDisplayTimer = 0f;
    private string keyPressMessage = "";
    private GUIStyle infoStyle;
    private GUIStyle keyPressStyle;

    private int RectHigh = 400;

    private float wheelSensitivity = 5f;

    void Start()
    {
        playerCamera = GetComponent<Camera>();
        if (playerCamera == null)
        {
            playerCamera = Camera.main;
        }

        if (mazePlugin == null)
            mazePlugin = FindFirstObjectByType<MazePlugin>();
        if (pathVisualizer == null)
            pathVisualizer = FindFirstObjectByType<PathfindingVisualizer>();

        LockCursor(true);
        rotationX = transform.eulerAngles.y;
        rotationY = -transform.eulerAngles.x;

        InitializeGUIStyles();
    }

    void InitializeGUIStyles()
    {
        infoStyle = new GUIStyle();
        infoStyle.normal.textColor = Color.white;
        infoStyle.fontSize = 14;
        infoStyle.fontStyle = FontStyle.Bold;
        infoStyle.alignment = TextAnchor.UpperLeft;

        keyPressStyle = new GUIStyle();
        keyPressStyle.normal.textColor = Color.yellow;
        keyPressStyle.fontSize = 16;
        keyPressStyle.fontStyle = FontStyle.Bold;
        keyPressStyle.alignment = TextAnchor.UpperLeft;
    }

    void Update()
    {
        if (keyPressDisplayTimer <= 0)
        {
            RectHigh = 380;
        }

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            ToggleCursorLock();
        }

        if (isCursorLocked)
        {
            HandleRotation();
        }

        HandleMovement();
        HandleKeyboardControls();
        HandleMouseWheelControls();

        if (keyPressDisplayTimer > 0)
        {
            keyPressDisplayTimer -= Time.deltaTime;
        }
    }

    void HandleRotation()
    {
        float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
        float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

        rotationX += mouseX;
        rotationY += mouseY;
        rotationY = Mathf.Clamp(rotationY, -90f, 90f);

        transform.rotation = Quaternion.Euler(-rotationY, rotationX, 0f);
    }

    void HandleMovement()
    {
        float horizontal = Input.GetAxis("Horizontal");
        float vertical = Input.GetAxis("Vertical");
        
        Vector3 moveDirection = Vector3.zero;
        
        if (horizontal != 0 || vertical != 0)
        {
            Vector3 forward = transform.forward;
            Vector3 right = transform.right;
            
            forward.y = 0f;
            right.y = 0f;
            forward.Normalize();
            right.Normalize();
            
            moveDirection = (forward * vertical + right * horizontal).normalized;
        }

        float climbInput = 0f;
        if (Input.GetKey(KeyCode.Space))
        {
            climbInput = 1f;
        }
        else if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.C))
        {
            climbInput = -1f;
        }

        Vector3 climbDirection = Vector3.up * climbInput;

        float targetSpeed = moveSpeed;
        if (Input.GetKey(KeyCode.LeftControl))
        {
            targetSpeed *= sprintMultiplier;
        }

        currentSpeed = targetSpeed;

        Vector3 targetVelocity = (moveDirection * currentSpeed) + (climbDirection * climbSpeed);
        currentVelocity = Vector3.SmoothDamp(currentVelocity, targetVelocity, ref smoothMoveVelocity, smoothTime);
        transform.position += currentVelocity * Time.deltaTime;
    }

    void HandleKeyboardControls()
    {
        if (Input.GetKey(KeyCode.U))
        {
            if (Input.GetKeyDown(KeyCode.LeftBracket))
            {
                if (mazePlugin != null) mazePlugin.Len++;
                ShowKeyPress("U + [: Len 增加");
            }
            else if (Input.GetKeyDown(KeyCode.RightBracket))
            {
                if (mazePlugin != null) mazePlugin.Len = Mathf.Max(2, mazePlugin.Len - 1);
                ShowKeyPress("U + ]: Len 减少");
            }
        }

        if (Input.GetKey(KeyCode.I))
        {
            if (Input.GetKeyDown(KeyCode.LeftBracket))
            {
                if (mazePlugin != null) mazePlugin.Wid++;
                ShowKeyPress("I + [: Wid 增加");
            }
            else if (Input.GetKeyDown(KeyCode.RightBracket))
            {
                if (mazePlugin != null) mazePlugin.Wid = Mathf.Max(2, mazePlugin.Wid - 1);
                ShowKeyPress("I + ]: Wid 减少");
            }
        }

        if (Input.GetKey(KeyCode.O))
        {
            if (Input.GetKeyDown(KeyCode.LeftBracket))
            {
                if (mazePlugin != null) mazePlugin.FramesPerSecond++;
                ShowKeyPress("O + [: 生成速度 增加");
            }
            else if (Input.GetKeyDown(KeyCode.RightBracket))
            {
                if (mazePlugin != null) mazePlugin.FramesPerSecond = Mathf.Max(1f, mazePlugin.FramesPerSecond - 1);
                ShowKeyPress("O + ]: 生成速度 减少");
            }
        }

        if (Input.GetKey(KeyCode.P))
        {
            if (Input.GetKeyDown(KeyCode.LeftBracket))
            {
                if (pathVisualizer != null) pathVisualizer.framesPerSecond++;
                ShowKeyPress("P + [: 寻路速度 增加");
            }
            else if (Input.GetKeyDown(KeyCode.RightBracket))
            {
                if (pathVisualizer != null) pathVisualizer.framesPerSecond = Mathf.Max(1f, pathVisualizer.framesPerSecond - 1);
                ShowKeyPress("P + ]: 寻路速度 减少");
            }
        }

        if (Input.GetKeyDown(KeyCode.R))
        {
            if (mazePlugin != null)
            {
                mazePlugin.RegenerateMaze();
                ShowKeyPress("R: 重新生成迷宫");
            }
        }

        if (Input.GetKeyDown(KeyCode.J))
        {
            if (mazePlugin != null && mazePlugin.isMazeReadyForPathfinding)
            {
                mazePlugin.StartPathfinding(PathfindingDataProvider.PathfindingAlgorithm.DFS);
                ShowKeyPress("J: 开始DFS寻路");
            }
        }

        if (Input.GetKeyDown(KeyCode.K))
        {
            if (mazePlugin != null && mazePlugin.isMazeReadyForPathfinding)
            {
                mazePlugin.StartPathfinding(PathfindingDataProvider.PathfindingAlgorithm.BFS);
                ShowKeyPress("K: 开始BFS寻路");
            }
        }

        if (Input.GetKeyDown(KeyCode.L))
        {
            if (mazePlugin != null)
            {
                mazePlugin.StopPathfinding();
                ShowKeyPress("L: 停止寻路");
            }
        }
    }

    void HandleMouseWheelControls()
    {
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        
        if (scroll != 0)
        {
            if (Input.GetKey(KeyCode.U))
            {
                // 根据滚轮方向调整 Len
                int change = Mathf.RoundToInt(scroll * wheelSensitivity);
                if (change == 0) change = scroll > 0 ? 1 : -1;

                if (mazePlugin != null)
                {
                    mazePlugin.Len = Mathf.Max(2, mazePlugin.Len + change);
                    ShowKeyPress($"U + 滚轮: Len {(change > 0 ? "增加" : "减少")}");
                }
            }
            else if (Input.GetKey(KeyCode.I))
            {
                int change = Mathf.RoundToInt(scroll * wheelSensitivity);
                if (change == 0) change = scroll > 0 ? 1 : -1;

                if (mazePlugin != null)
                {
                    mazePlugin.Wid = Mathf.Max(2, mazePlugin.Wid + change);
                    ShowKeyPress($"I + 滚轮: Wid {(change > 0 ? "增加" : "减少")}");
                }
            }
            else if (Input.GetKey(KeyCode.O))
            {
                float change = scroll * wheelSensitivity;
                if (Mathf.Abs(change) < 1) change = scroll > 0 ? 1 : -1;

                if (mazePlugin != null)
                {
                    mazePlugin.FramesPerSecond = Mathf.Max(1f, mazePlugin.FramesPerSecond + change);
                    ShowKeyPress($"O + 滚轮: 生成速度 {(change > 0 ? "增加" : "减少")}");
                }
            }
            else if (Input.GetKey(KeyCode.P))
            {
                float change = scroll * wheelSensitivity;
                if (Mathf.Abs(change) < 1) change = scroll > 0 ? 1 : -1;

                if (pathVisualizer != null)
                {
                    pathVisualizer.framesPerSecond = Mathf.Max(1f, pathVisualizer.framesPerSecond + change);
                    ShowKeyPress($"P + 滚轮: 寻路速度 {(change > 0 ? "增加" : "减少")}");
                }
            }
        }
    }

    void ShowKeyPress(string message)
    {
        keyPressMessage = message;
        RectHigh = 430;
        keyPressDisplayTimer = 2f; 
        Debug.Log(message);
    }

    void OnGUI()
    {
        if (infoStyle == null) InitializeGUIStyles();

        Rect infoRect = new Rect(10, 10, 300, RectHigh);
        
        string displayText = "=== 相机控制 ===\n";
        displayText += $"位置: {transform.position.ToString("F1")}\n";
        displayText += $"速度: {currentVelocity.magnitude:F1}\n\n";
        
        displayText += "=== 迷宫参数控制 ===\n";
        displayText += $"U+[/]或滚轮  长度: {(mazePlugin != null ? mazePlugin.Len.ToString() : "N/A")}\n";
        displayText += $"I+[/]或滚轮  宽度: {(mazePlugin != null ? mazePlugin.Wid.ToString() : "N/A")}\n";
        displayText += $"O+[/]或滚轮  生成速度: {(mazePlugin != null ? ((int)mazePlugin.FramesPerSecond).ToString() : "N/A")}\n";
        displayText += $"P+[/]或滚轮  寻路速度: {(pathVisualizer != null ? ((int)pathVisualizer.framesPerSecond).ToString() : "N/A")}\n";
        displayText += $"R: 重新生成迷宫\n";
        displayText += $"J: DFS寻路 | K: BFS寻路 | L: 停止寻路\n\n";
        
        if (mazePlugin != null)
        {
            displayText += "=== 迷宫状态 ===\n";
            displayText += GetMazeInfo() + "\n" + "\n";
        }

        if (keyPressDisplayTimer > 0)
        {
            displayText += $"=== 操作反馈 ===\n";
            displayText += $"{keyPressMessage}";
        }
        
        GUI.Box(new Rect(infoRect.x - 5, infoRect.y - 5, infoRect.width + 10, infoRect.height + 10), "");
        
        GUI.Label(infoRect, displayText, infoStyle);

        if (!isCursorLocked)
        {
            GUIStyle centerStyle = new GUIStyle();
            centerStyle.normal.textColor = Color.blue;
            centerStyle.fontSize = 20;
            centerStyle.fontStyle = FontStyle.Bold;
            centerStyle.alignment = TextAnchor.MiddleCenter;
            
            GUI.Label(new Rect(Screen.width / 2 - 150, Screen.height / 2 - 25, 300, 50), 
                     "点击屏幕进入自由移动模式\n按 ESC 再次锁定鼠标", centerStyle);
        }
    }

    private string GetMazeInfo()
    {
        if (mazePlugin == null) 
        {
            mazePlugin = FindFirstObjectByType<MazePlugin>();
            if (mazePlugin == null) return "未找到MazePlugin";
        }

        try
        {
            return mazePlugin.GetMazeInfoString();
        }
        catch (System.Exception e)
        {
            return $"迷宫信息获取失败: {e.Message}";
        }
    }

    void LockCursor(bool locked)
    {
        isCursorLocked = locked;
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    void ToggleCursorLock()
    {
        LockCursor(!isCursorLocked);
    }
}