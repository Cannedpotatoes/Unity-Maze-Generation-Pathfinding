using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

public class MazePlugin : MonoBehaviour
{
    [Header("Maze Size")]
    [SerializeField] public int len = 30;
    [SerializeField] public int wid  = 30;
    [SerializeField] private int blen = 1;

    [Header("Start&End Points/Auto-Generate")]
    [SerializeField] private int startX = 1;
    [SerializeField] private int startY = 1;
    [SerializeField] private int endX = 8;
    [SerializeField] private int endY = 8;
    [SerializeField] private float distanceCOES2E  = 0.67f;

    [Header("Ground&Wall Materials")]
    [SerializeField] private Material groundMaterial;
    [SerializeField] private Material wallMaterial;

    [Header("Start&End Materials")]
    [SerializeField] private Material startMaterial;
    [SerializeField] private Material endMaterial;
    [Header("Start&End Point Size")]
    [SerializeField] private float thePointSideLength  = 0.3f;
    [SerializeField] private float thePointHigh  = 3.0f;

    [Header("Visualization Settings")]
    [SerializeField] private float aniDuration  = 0.1f;
    [SerializeField] public float framesPerSecond = 60f;
    [SerializeField] private bool showGenerationProcess = true;
    [SerializeField] private int batchProcessCount = 15;

    public int Length => len;
    public int Width => wid;
    public Vector2Int StartPoint => new Vector2Int(startX, startY);
    public Vector2Int EndPoint => new Vector2Int(endX, endY);

    private MazeDataProvider dataProvider;
    private Coroutine visualizationCoroutine;
    private bool isVisualizing = false;

    private GameObject groundObject;
    private GameObject wallsParent;
    private GameObject startPointObject;
    private GameObject endPointObject;

    private Dictionary<Vector2Int, GameObject> wallObjects = new Dictionary<Vector2Int, GameObject>();
    private HashSet<Vector2Int> wallsToCreate = new HashSet<Vector2Int>();
    private HashSet<Vector2Int> wallsToRemove = new HashSet<Vector2Int>();

    private int[,] currentMazeState;
    private bool isFirstFrame = true;
    private bool regenerationRequested = false;

    private PathfindingVisualizer pathfindingVisualizer;
    private PathfindingDataProvider pathfindingProvider;
    public bool isMazeReadyForPathfinding = false;

    private bool mazeResourcesCleaned = false;

    void Start()
    {
        //确保主线程调度器存在
        if (UnityMainThreadDispatcher.Instance == null)
        {
            Debug.LogError("UnityMainThreadDispatcher 实例不存在!");
            return;
        }

        dataProvider = gameObject.AddComponent<MazeDataProvider>();
        dataProvider.OnGenerationComplete += OnDataGenerationComplete;

        InitializePathfinding();
        InitializeMaze();

    }

    void InitializeMaze()
    {
        CreateGround();
        StartMazeGeneration();
    }

    void CreateGround()
    {
        if (groundObject != null)
            DestroyImmediate(groundObject);

        groundObject = GameObject.CreatePrimitive(PrimitiveType.Plane);
        groundObject.name = "Ground";
        groundObject.transform.position = new Vector3(len * 0.5f - 0.5f, 0, wid * 0.5f - 0.5f);
        groundObject.transform.localScale = new Vector3(len * 0.1f, 1, wid * 0.1f);

        Renderer groundRenderer = groundObject.GetComponent<Renderer>();
        if (groundMaterial != null)
        {
            groundRenderer.material = groundMaterial;
        }
        else
        {
            groundRenderer.material.color = Color.gray;
        }
    }

    void StartMazeGeneration()
    {
        if (regenerationRequested)
        {
            Debug.Log("重新生成请求已存在，跳过此次调用");
            return;
        }

        Debug.Log("开始迷宫生成过程...");

        SafeStopAllCoroutines();

        if (startPointObject != null)
        {
            DestroyImmediate(startPointObject);
            startPointObject = null;
        }
        if (endPointObject != null)
        {
            DestroyImmediate(endPointObject);
            endPointObject = null;
        }

        if (pathfindingVisualizer != null)
        {
            pathfindingVisualizer.CleanupVisualizationImmediate();
        }

        CleanupWalls();
        CreateWallFence();

        isFirstFrame = true;
        wallsToCreate.Clear();
        wallsToRemove.Clear();
        currentMazeState = new int[len, wid];

        dataProvider.StartGeneration(len, wid, blen);
        visualizationCoroutine = StartCoroutine(VisualizationProcess());
    }

    IEnumerator VisualizationProcess()
    {
        isVisualizing = true;
        regenerationRequested = false;

        Debug.Log("等待迷宫数据...");

        int waitFrames = 0;
        while ((!dataProvider.HasFrames() || waitFrames < 30) && !dataProvider.IsGenerationFinished() && waitFrames < 500)
        {
            if (regenerationRequested || this == null) yield break;
            waitFrames++;
            if (waitFrames % 50 == 0)
            {
                Debug.Log($"等待迷宫数据... 已等待 {waitFrames * 0.1f} 秒");
            }
            yield return new WaitForSeconds(0.1f);
        }

        if (!dataProvider.HasFrames())
        {
            Debug.LogWarning($"未收到迷宫数据，等待了 {waitFrames * 0.1f} 秒后使用备用生成");
            GenerateFallbackMazeData();
            ApplyBatchUpdates();
            if (!regenerationRequested && this != null)
                CreateStartAndEndPoints();
            yield break;
        }

        float frameInterval = 1f / framesPerSecond;//帧间隔
        float lastFrameTime = 0f;//上一帧处理时间
        int processedFrames = 0;//已处理帧计时器
        int lastWallCount = 0;//上一帧墙
        int sameWallCountFrames = 0;//墙数量保持不变的连续帧数

        //预期墙数量估算
        int mazeSize = len * wid;
        int maxStableFrames = Mathf.Clamp(mazeSize / 50, 30, 200); //最大稳定帧数
        int minRequiredWalls = Mathf.Clamp(mazeSize / 3, 10, mazeSize); //至少1/3个格子是墙
        int expectedWalls = Mathf.Clamp((int)(mazeSize * 0.6f), minRequiredWalls, mazeSize - 10);
        Debug.Log($"开始可视化，迷宫大小: {len}x{wid} ({mazeSize}格)，预期墙数量: {expectedWalls}，稳定阈值: {maxStableFrames}帧");

        //主循环
        while (isVisualizing && !regenerationRequested && this != null &&
            (dataProvider.HasFrames() || !dataProvider.IsGenerationFinished()))
        {
            if (Time.time - lastFrameTime >= frameInterval)
            {
                int framesProcessedThisBatch = 0;
                bool hasValidDataThisBatch = false;

                //批量处理多个帧
                while (dataProvider.TryGetNextFrame(out int[] frameData) && framesProcessedThisBatch < batchProcessCount)
                {
                    if (regenerationRequested || this == null) break;

                    if (frameData != null && frameData.Length == len * wid)
                    {
                        ProcessMazeFrame(frameData);//处理单帧数据
                        framesProcessedThisBatch++;
                        processedFrames++;
                        hasValidDataThisBatch = true;
                    }
                }

                //应用批量更新
                if (!regenerationRequested && this != null && hasValidDataThisBatch)
                {
                    ApplyBatchUpdates();
                }

                //检测墙数量是否合理
                int currentWallCount = wallObjects.Count;
                float completionRatio = (float)currentWallCount / expectedWalls;

                if (dataProvider.IsGenerationFinished())
                {
                    if (currentWallCount == lastWallCount)
                    {
                        sameWallCountFrames++;

                        bool hasMinimalWalls = currentWallCount >= minRequiredWalls;
                        bool isStableEnough = sameWallCountFrames > maxStableFrames;
                        bool hasReasonableCompletion = completionRatio > 0.4f;

                        if (hasMinimalWalls && isStableEnough && hasReasonableCompletion)
                        {
                            Debug.Log($"生成稳定结束: 墙数量 {currentWallCount} (完成度 {completionRatio * 100f:F1}%) 稳定 {sameWallCountFrames} 帧");
                            break;
                        }
                        else if (sameWallCountFrames > maxStableFrames * 2)
                        {
                            //如果稳定时间过长也强制结束
                            Debug.Log($"强制结束生成: 已稳定 {sameWallCountFrames} 帧，当前墙数量: {currentWallCount}");
                            break;
                        }
                    }
                    else
                    {
                        sameWallCountFrames = 0;
                        lastWallCount = currentWallCount;
                    }
                }
                lastFrameTime = Time.time;

                if (processedFrames % 100 == 0)
                {
                    Debug.Log($"已处理 {processedFrames} 帧，当前墙数量: {wallObjects.Count} ");
                }
            }

            yield return null;
        }

        // 最终检查
        int finalWallCount = wallObjects.Count;
        float finalCompletionRatio = (float)finalWallCount / expectedWalls;

        if (!regenerationRequested && this != null)
        {
            Debug.Log($"可视化完成，共处理 {processedFrames} 帧，最终墙数量: {finalWallCount} ");

            if (finalCompletionRatio < 0.3f)
            {
                Debug.LogWarning("最终墙数量较少，迷宫可能不完整？");
            }

            CreateStartAndEndPoints();
        }
        else
        {
            Debug.Log("可视化被重新生成请求中断");
        }

        isVisualizing = false;
    }

    void ProcessMazeFrame(int[] frameData)
    {
        if (frameData == null || frameData.Length != len * wid) return;

        try
        {
            //转换为二维数组
            int[,] newState = new int[len, wid];
            for (int x = 0; x < len; x++)
            {
                for (int y = 0; y < wid; y++)
                {
                    int index = y * len + x;
                    newState[x, y] = frameData[index];
                }
            }

            //第一帧直接创建所有墙
            if (isFirstFrame)
            {
                for (int x = 0; x < len; x++)
                {
                    for (int y = 0; y < wid; y++)
                    {
                        if (newState[x, y] == 2)
                        {
                            wallsToCreate.Add(new Vector2Int(x, y));
                        }
                    }
                }
                isFirstFrame = false;
            }
            else
            {
                //性能优化，比较状态，找出变化的墙
                for (int x = 0; x < len; x++)
                {
                    for (int y = 0; y < wid; y++)
                    {
                        Vector2Int pos = new Vector2Int(x, y);
                        bool wasWall = currentMazeState[x, y] == 2;
                        bool isWall = newState[x, y] == 2;

                        if (wasWall && !isWall)
                        {
                            wallsToRemove.Add(pos);
                            wallsToCreate.Remove(pos);
                        }
                        else if (!wasWall && isWall)
                        {
                            wallsToCreate.Add(pos);
                            wallsToRemove.Remove(pos);
                        }
                    }
                }
            }

            currentMazeState = newState;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"处理迷宫帧时出错: {e.Message}");
        }
    }

    void ApplyBatchUpdates()
    {
        foreach (var pos in wallsToRemove)
        {
            if (wallObjects.ContainsKey(pos))
            {
                DestroyImmediate(wallObjects[pos]);
                wallObjects.Remove(pos);
            }
        }
        wallsToRemove.Clear();

        int createdThisFrame = 0;
        var wallsToCreateCopy = new List<Vector2Int>(wallsToCreate);
        foreach (var pos in wallsToCreateCopy)
        {
            if (!wallObjects.ContainsKey(pos) && createdThisFrame < 40)
            {
                CreateWallAt(pos.x, pos.y);
                createdThisFrame++;
                wallsToCreate.Remove(pos);
            }
        }
    }

    void CreateWallAt(int x, int y)
    {
        Vector2Int pos = new Vector2Int(x, y);

        if (wallObjects.ContainsKey(pos))
            return;

        Vector3 position = new Vector3(x, 0.5f, y);
        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.transform.position = position;
        wall.name = $"Wall_{x}_{y}";

        Renderer wallRenderer = wall.GetComponent<Renderer>();
        if (wallMaterial != null)
        {
            wallRenderer.material = wallMaterial;
        }
        else
        {
            wallRenderer.material.color = Color.red;
        }

        if (showGenerationProcess)
        {
            StartCoroutine(PlayScaleUpAnimation(wall));
        }

        if (wallsParent != null)
            wall.transform.SetParent(wallsParent.transform);

        wallObjects[pos] = wall;
    }

    IEnumerator PlayScaleUpAnimation(GameObject wall)
    {
        if (wall == null) yield break;

        Vector3 originalScale = Vector3.one;
        float elapsedTime = 0f;

        wall.transform.localScale = Vector3.zero;

        while (elapsedTime < aniDuration && wall != null)
        {
            float progress = elapsedTime / aniDuration;
            wall.transform.localScale = Vector3.one * progress;
            elapsedTime += Time.deltaTime;
            yield return null;
        }

        if (wall != null)
            wall.transform.localScale = originalScale;
    }

    void CreateWallFence()
    {
        if (wallsParent != null)
            DestroyImmediate(wallsParent);

        wallsParent = new GameObject("Walls");

        for (int x = -1; x <= len; x++)
        {
            CreateStaticWallAt(x, -1);
            CreateStaticWallAt(x, wid);
        }
        for (int y = 0; y < wid; y++)
        {
            CreateStaticWallAt(-1, y);
            CreateStaticWallAt(len, y);
        }
    }

    void CreateStaticWallAt(int x, int y)
    {
        Vector3 position = new Vector3(x, 0.5f, y);
        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.transform.position = position;
        wall.name = $"Fence_{x}_{y}";

        Renderer wallRenderer = wall.GetComponent<Renderer>();
        if (wallMaterial != null)
        {
            wallRenderer.material = wallMaterial;
        }
        else
        {
            wallRenderer.material.color = Color.black;
        }

        if (wallsParent != null)
            wall.transform.SetParent(wallsParent.transform);
    }

    void CreateStartAndEndPoints()
    {
        if (startPointObject != null) DestroyImmediate(startPointObject);
        if (endPointObject != null) DestroyImmediate(endPointObject);

        FindStartAndEndPositions();

        EnsureStartEndNotTrapped();

        ValidateStartEndPositions();

        startPointObject = CreatePointMarker(startX, startY, "StartPoint", startMaterial != null ? startMaterial : CreateDefaultMaterial(Color.green));
        endPointObject = CreatePointMarker(endX, endY, "EndPoint", endMaterial != null ? endMaterial : CreateDefaultMaterial(Color.blue));

        Debug.Log($"创建起点({startX},{startY})和终点({endX},{endY})");
    }

    private void EnsureStartEndNotTrapped()
    {
        if (IsPositionTrapped(startX, startY))
        {
            Debug.LogWarning($"起点({startX},{startY})被完全封闭，重新选择...");
            Vector2Int newStart = FindRandomUntrappedPosition();
            startX = newStart.x;
            startY = newStart.y;
            Debug.Log($"新起点: ({startX},{startY})");
        }

        if (IsPositionTrapped(endX, endY))
        {
            Debug.LogWarning($"终点({endX},{endY})被完全封闭，重新选择...");
            Vector2Int newEnd = FindRandomUntrappedPosition();
            endX = newEnd.x;
            endY = newEnd.y;
            Debug.Log($"新终点: ({endX},{endY})");
        }
    }

    private bool IsPositionTrapped(int x, int y)
    {
        int trappedDirections = 0;
        int totalDirections = 0;

        if (y + 1 < wid)
        {
            totalDirections++;
            if (currentMazeState[x, y + 1] == 2) trappedDirections++;
        }
        if (y - 1 >= 0)
        {
            totalDirections++;
            if (currentMazeState[x, y - 1] == 2) trappedDirections++;
        }
        if (x - 1 >= 0)
        {
            totalDirections++;
            if (currentMazeState[x - 1, y] == 2) trappedDirections++;
        }
        if (x + 1 < len)
        {
            totalDirections++;
            if (currentMazeState[x + 1, y] == 2) trappedDirections++;
        }

        return trappedDirections == totalDirections && totalDirections > 0;
    }

    private Vector2Int FindRandomUntrappedPosition()
    {
        List<Vector2Int> untrappedPositions = new List<Vector2Int>();

        //收集所有不被封闭的可行走位置
        for (int x = 0; x < len; x++)
        {
            for (int y = 0; y < wid; y++)
            {
                if (IsPositionWalkable(x, y) && !IsPositionTrapped(x, y))
                {
                    untrappedPositions.Add(new Vector2Int(x, y));
                }
            }
        }

        if (untrappedPositions.Count == 0)
        {
            Debug.LogError("没有找到不被封闭的位置！使用默认位置(1,1)");
            return new Vector2Int(1, 1);
        }

        //随机选择一个
        int randomIndex = UnityEngine.Random.Range(0, untrappedPositions.Count);
        return untrappedPositions[randomIndex];
    }

    GameObject CreatePointMarker(int x, int y, string name, Material material)
    {
        Vector3 position = new Vector3(x, 0.5f, y);
        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
        marker.name = name;
        marker.transform.position = position;
        marker.transform.localScale = new Vector3(thePointSideLength, thePointHigh, thePointSideLength);

        Renderer renderer = marker.GetComponent<Renderer>();
        renderer.material = material;

        return marker;
    }

    Material CreateDefaultMaterial(Color color)
    {
        Material mat = new Material(Shader.Find("Standard"));
        mat.color = color;
        return mat;
    }

    void ValidateStartEndPositions()
    {
        startX = Mathf.Clamp(startX, 0, len - 1);
        startY = Mathf.Clamp(startY, 0, wid - 1);
        endX = Mathf.Clamp(endX, 0, len - 1);
        endY = Mathf.Clamp(endY, 0, wid - 1);
    }

    void CleanupWalls()
    {
        SafeStopAllCoroutines();

        foreach (var wall in wallObjects.Values)
        {
            if (wall != null)
                DestroyImmediate(wall);
        }
        wallObjects.Clear();
        wallsToCreate.Clear();
        wallsToRemove.Clear();

        if (wallsParent != null)
            DestroyImmediate(wallsParent);
    }

    void SafeStopAllCoroutines()
    {
        if (visualizationCoroutine != null)
        {
            StopCoroutine(visualizationCoroutine);
            visualizationCoroutine = null;
        }

        StopAllCoroutines();
    }

    void GenerateFallbackMazeData()
    {
        Debug.Log("使用备用迷宫数据生成");
        currentMazeState = new int[len, wid];

        //随机生成(一个点有25%的概率生成墙)
        for (int x = 0; x < len; x++)
        {
            for (int y = 0; y < wid; y++)
            {
                if (Random.Range(0, 100) < 25 &&
                    (x != startX || y != startY) &&
                    (x != endX || y != endY))
                {
                    currentMazeState[x, y] = 2;
                    wallsToCreate.Add(new Vector2Int(x, y));
                }
            }
        }
    }

    public string GetMazeInfoString()
    {
        string pathfindingStatus;
        string currentAlgorithm;

        if (pathfindingVisualizer != null && pathfindingVisualizer.IsPathfindingRunning)
        {
            pathfindingStatus = "寻路中";
            currentAlgorithm = pathfindingVisualizer.GetCurrentAlgorithm().ToString();
        }
        else
        {
            pathfindingStatus = "未寻路";
            currentAlgorithm = "无";
        }

        return $"迷宫大小: {len}x{wid}\n" +
            $"起点: ({startX},{startY}) 终点: ({endX},{endY})\n" +
            $"显示生成过程: {showGenerationProcess}\n" +
            $"当前墙数量: {wallObjects.Count}\n" +
            $"生成状态: {(dataProvider.IsGenerating ? "生成中" : "已完成")}\n" +
            $"寻路状态(显示滞后): {pathfindingStatus}\n" +
            $"当前寻路算法(显示滞后): {currentAlgorithm}";
    }

    void Update()
    {
    }

    public void RegenerateMaze()
    {
        if (regenerationRequested)
        {
            Debug.Log("重新生成请求已在进行中，跳过重复请求");
            return;
        }

        regenerationRequested = true;
        isVisualizing = false;
        isMazeReadyForPathfinding = false;

        Debug.Log("开始重新生成迷宫");

        SafeStopAllCoroutines();

        if (startPointObject != null)
        {
            DestroyImmediate(startPointObject);
            startPointObject = null;
        }
        
        if (endPointObject != null)
        {
            DestroyImmediate(endPointObject);
            endPointObject = null;
        }

        if (pathfindingVisualizer != null)
        {
            pathfindingVisualizer.StopPathfindingVisualization();
            pathfindingVisualizer.CleanupVisualizationImmediate();
        }

        if (dataProvider != null && !mazeResourcesCleaned)
        {
            dataProvider.CleanupMazeResources();
            mazeResourcesCleaned = true;
        }

        StartCoroutine(DelayedRegeneration());
    }

    IEnumerator DelayedRegeneration()
    {
        yield return null;

        CleanupWalls();

        if (groundObject != null)
        {
            groundObject.transform.position = new Vector3(len * 0.5f - 0.5f, 0, wid * 0.5f - 0.5f);
            groundObject.transform.localScale = new Vector3(len * 0.1f, 1, wid * 0.1f);
        }

        regenerationRequested = false;
        mazeResourcesCleaned = false;
        StartMazeGeneration();
    }

    private void FindStartAndEndPositions()
    {
        List<Vector2Int> walkablePositions = new List<Vector2Int>();

        for (int x = 0; x < len; x++)
        {
            for (int y = 0; y < wid; y++)
            {
                if (currentMazeState != null && currentMazeState[x, y] != 2)
                {
                    walkablePositions.Add(new Vector2Int(x, y));
                }
            }
        }

        if (walkablePositions.Count < 2)
        {
            Debug.LogWarning("可通行位置不足，使用默认起点终点"); //其实这种情况基本不可能
            startX = 1;
            startY = 1;
            endX = len - 2;
            endY = wid - 2;
            return;
        }

        //最小距离（默认迷宫对角线长度的2/3）
        float minDistance = Mathf.Sqrt(len * len + wid * wid) * distanceCOES2E;

        //尝试多次寻找合适的起点终点对
        int maxAttempts = 100;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            //随机选择起点和终点
            int startIndex = UnityEngine.Random.Range(0, walkablePositions.Count);
            int endIndex = UnityEngine.Random.Range(0, walkablePositions.Count);

            if (startIndex == endIndex) continue;

            Vector2Int startPos = walkablePositions[startIndex];
            Vector2Int endPos = walkablePositions[endIndex];

            float distance = Vector2.Distance(new Vector2(startPos.x, startPos.y),
                                            new Vector2(endPos.x, endPos.y));

            //检查距离是否满足要求
            if (distance >= minDistance)
            {
                startX = startPos.x;
                startY = startPos.y;
                endX = endPos.x;
                endY = endPos.y;

                Debug.Log($"找到合适的起点终点: 起点({startX},{startY}) 终点({endX},{endY}) 距离: {distance:F1}");
                return;
            }
        }

        FindFarthestPositions(walkablePositions);
    }

    private void FindFarthestPositions(List<Vector2Int> walkablePositions)
    {
        if (walkablePositions.Count < 2) return;

        float maxDistance = 0;
        Vector2Int bestStart = walkablePositions[0];
        Vector2Int bestEnd = walkablePositions[1];

        //采样部分位置对以提高性能（对于大迷宫）
        int sampleSize = Mathf.Min(50, walkablePositions.Count);

        for (int i = 0; i < sampleSize; i++)
        {
            int startIndex = UnityEngine.Random.Range(0, walkablePositions.Count);
            Vector2Int startPos = walkablePositions[startIndex];

            for (int j = 0; j < sampleSize; j++)
            {
                if (i == j) continue;

                int endIndex = UnityEngine.Random.Range(0, walkablePositions.Count);
                Vector2Int endPos = walkablePositions[endIndex];

                float distance = Vector2.Distance(new Vector2(startPos.x, startPos.y),
                                                new Vector2(endPos.x, endPos.y));

                if (distance > maxDistance)
                {
                    maxDistance = distance;
                    bestStart = startPos;
                    bestEnd = endPos;
                }
            }
        }

        startX = bestStart.x;
        startY = bestStart.y;
        endX = bestEnd.x;
        endY = bestEnd.y;

        Debug.Log($"选择最远位置对: 起点({startX},{startY}) 终点({endX},{endY}) 距离: {maxDistance:F1}");
    }

    //获取迷宫数据（供寻路算法使用）
    public int[,] GetMazeData()
    {
        return currentMazeState;
    }

    //检查指定位置是否可通行
    public bool IsPositionWalkable(int x, int y)
    {
        if (currentMazeState == null) return false;
        if (x < 0 || x >= len || y < 0 || y >= wid) return false;

        return currentMazeState[x, y] != 2;
    }
    public bool IsPositionWalkable(Vector2Int position)
    {
        return IsPositionWalkable(position.x, position.y);
    }

    //初始化寻路系统
    private void InitializePathfinding()
    {
        pathfindingVisualizer = GetComponent<PathfindingVisualizer>();
        if (pathfindingVisualizer == null)
        {
            pathfindingVisualizer = gameObject.AddComponent<PathfindingVisualizer>();
            Debug.Log("自动添加PathfindingVisualizer组件");
        }

        pathfindingProvider = GetComponent<PathfindingDataProvider>();
        if (pathfindingProvider == null)
        {
            pathfindingProvider = gameObject.AddComponent<PathfindingDataProvider>();
            Debug.Log("自动添加PathfindingDataProvider组件");
        }
    }

    void OnDataGenerationComplete()
    {
        Debug.Log("迷宫状态已就绪，可用于寻路");
        isMazeReadyForPathfinding = true;
    }

    //寻路控制
    public void StartPathfinding(PathfindingDataProvider.PathfindingAlgorithm algorithm)
    {
        if (!isMazeReadyForPathfinding)
        {
            Debug.LogWarning("迷宫尚未生成完成，无法开始寻路");
            return;
        }

        //检查寻路数据提供器是否可用
        if (pathfindingProvider != null && !pathfindingProvider.IsDllAvailable())
        {
            Debug.LogWarning("寻路系统正忙，请稍后再试");
            return;
        }

        if (pathfindingVisualizer != null)
        {
            pathfindingVisualizer.StartPathfindingVisualization(algorithm);
        }
        else
        {
            Debug.LogError("PathfindingVisualizer 未初始化!");
        }
    }

    public void StopPathfinding()
    {
        if (pathfindingVisualizer != null)
        {
            pathfindingVisualizer.StopPathfindingVisualization();
        }
    }

    public PathfindingDataProvider.PathfindingAlgorithm GetCurrentPathfindingAlgorithm()
    {
        if (pathfindingVisualizer != null)
        {
            return pathfindingVisualizer.GetCurrentAlgorithm();
        }
        return PathfindingDataProvider.PathfindingAlgorithm.BFS;
    }

    void OnDestroy()
    {
        regenerationRequested = true;
        isVisualizing = false;

        Debug.Log("MazePlugin.OnDestroy 被调用，开始安全清理...");

        //停止并清理数据/线程（如果存在）
        try
        {
            if (dataProvider != null)
            {
                // 停止生成线程并清理Dll资源
                try { dataProvider.StopGeneration(); } catch { }
                try { dataProvider.CleanupMazeResources(); } catch { }
                try
                {
                    Destroy(dataProvider);
                }
                catch { }
                dataProvider = null;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"OnDestroy 清理 dataProvider 时异常: {e.Message}");
        }

        //停止寻路（如果有）
        try
        {
            if (pathfindingProvider != null)
            {
                try { pathfindingProvider.StopPathfinding(); } catch { }
                try { Destroy(pathfindingProvider); } catch { }
                pathfindingProvider = null;
            }

            if (pathfindingVisualizer != null)
            {
                try { pathfindingVisualizer.StopPathfindingVisualization(); } catch { }
                try { Destroy(pathfindingVisualizer); } catch { }
                pathfindingVisualizer = null;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"OnDestroy 清理寻路模块时异常: {e.Message}");
        }

        //停止并移除所有协程（在销毁前停止）
        try
        {
            SafeStopAllCoroutines();
            StopAllCoroutines();
        }
        catch { }

        //清理墙体、起点终点、地面
        try
        {
            //清理墙体
            foreach (var kv in wallObjects)
            {
                var obj = kv.Value;
                if (obj != null)
                {
                    try { Destroy(obj); } catch { }
                }
            }
            wallObjects.Clear();
            wallsToCreate.Clear();
            wallsToRemove.Clear();

            //清理父物体和地面与标记
            if (wallsParent != null)
            {
                try { Destroy(wallsParent); } catch { }
                wallsParent = null;
            }

            if (groundObject != null)
            {
                try { Destroy(groundObject); } catch { }
                groundObject = null;
            }

            if (startPointObject != null)
            {
                try { Destroy(startPointObject); } catch { }
                startPointObject = null;
            }

            if (endPointObject != null)
            {
                try { Destroy(endPointObject); } catch { }
                endPointObject = null;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"OnDestroy 清理场景物体时异常: {e.Message}");
        }

        //销毁 UnityMainThreadDispatcher（如果它存在且仍在场景中）
        try
        {
            var dispatcher = GameObject.Find("UnityMainThreadDispatcher");
            if (dispatcher != null)
            {
                Debug.Log("OnDestroy: 发现 UnityMainThreadDispatcher，尝试安全销毁");
                try { Destroy(dispatcher); }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"销毁 UnityMainThreadDispatcher 时出错: {e.Message}");
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"OnDestroy 查找 UnityMainThreadDispatcher 时异常: {e.Message}");
        }

        mazeResourcesCleaned = true;

        Debug.Log("MazePlugin.OnDestroy 清理完成");
    }


    public int Len
    {
        get => len;
        set
        {
            len = Mathf.Max(2, value); 
        }
    }
    public int Wid
    {
        get => wid;
        set
        {
            wid = Mathf.Max(2, value);
        }
    }
    public float DistanceCOES2E
    {
        get => distanceCOES2E;
        set => distanceCOES2E = Mathf.Max(0f, value);
    }
    public float ThePointSideLength
    {
        get => thePointSideLength;
        set => thePointSideLength = Mathf.Max(0f, value);
    }
    public float ThePointHigh
    {
        get => thePointHigh;
        set => thePointHigh = Mathf.Max(0f, value);
    }
    public float AniDuration
    {
        get => aniDuration;
        set => aniDuration = Mathf.Max(0f, value);
    }

    public float FramesPerSecond
    {
        get => framesPerSecond;
        set => framesPerSecond = Mathf.Max(1f, value);
    }
}