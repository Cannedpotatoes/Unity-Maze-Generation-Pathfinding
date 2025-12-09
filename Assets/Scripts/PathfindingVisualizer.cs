using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class PathfindingVisualizer : MonoBehaviour
{
    [Header("Pathfinding Visualization")]
    [SerializeField] private Material visitedMaterial;
    [SerializeField] public float framesPerSecond = 10f;
    [SerializeField] private int batchProcessCount = 10;

    private MazePlugin mazePlugin;
    private PathfindingDataProvider pathfindingProvider;

    private Coroutine visualizationCoroutine;
    private bool isVisualizing = false;
    private bool isPathfindingRunning = false;
    private Dictionary<Vector2Int, GameObject> visitedObjects = new Dictionary<Vector2Int, GameObject>();
    private GameObject pathfindingParent;

    private PathfindingDataProvider.PathfindingAlgorithm currentAlgorithm = PathfindingDataProvider.PathfindingAlgorithm.BFS;

    private HashSet<Vector2Int> cellsToCreate = new HashSet<Vector2Int>();
    private HashSet<Vector2Int> cellsToRemove = new HashSet<Vector2Int>();
    private int[,] currentPathfindingState;
    private bool isFirstPathfindingFrame = true;

    public bool IsPathfindingRunning => isPathfindingRunning;
    void Start()
    {
        mazePlugin = GetComponent<MazePlugin>();
        if (mazePlugin == null)
        {
            Debug.LogError("MazePlugin 组件未找到! 请将PathfindingVisualizer与MazePlugin挂载到同一GameObject上");
            return;
        }

        pathfindingProvider = GetComponent<PathfindingDataProvider>();
        if (pathfindingProvider == null)
        {
            pathfindingProvider = gameObject.AddComponent<PathfindingDataProvider>();
            Debug.Log("自动添加PathfindingDataProvider组件");
        }

        pathfindingProvider.OnPathfindingComplete += OnPathfindingComplete;
        pathfindingProvider.OnPathfindingError += OnPathfindingError;

        CreatePathfindingParent();

        Debug.Log("PathfindingVisualizer初始化完成");
    }

    private void CreatePathfindingParent()
    {
        if (pathfindingParent != null)
            DestroyImmediate(pathfindingParent);

        pathfindingParent = new GameObject("PathfindingVisualization");
    }

    public void StartPathfindingVisualization(PathfindingDataProvider.PathfindingAlgorithm algorithm)
    {
        if (isVisualizing)
        {
            Debug.Log("正在可视化中，先停止之前的可视化");
            StopPathfindingVisualization();
        }

        if (mazePlugin == null)
        {
            Debug.LogError("MazePlugin 未找到!");
            return;
        }

        if (!mazePlugin.isMazeReadyForPathfinding)
        {
            Debug.LogError("迷宫尚未生成完成，无法寻路!");
            return;
        }

        if (pathfindingProvider == null)
        {
            Debug.LogError("PathfindingDataProvider 未找到!");
            return;
        }

        if (!mazePlugin.IsPositionWalkable(mazePlugin.StartPoint) ||
            !mazePlugin.IsPositionWalkable(mazePlugin.EndPoint))
        {
            Debug.LogError("起点或终点在墙上，无法寻路!");//<-基本不可能:)
            return;
        }

        currentAlgorithm = algorithm;

        isFirstPathfindingFrame = true;
        cellsToCreate.Clear();
        cellsToRemove.Clear();
        currentPathfindingState = new int[mazePlugin.Length, mazePlugin.Width];

        CleanupVisualization();
        CreatePathfindingParent();

        Debug.Log($"开始{currentAlgorithm}寻路可视化");

        pathfindingProvider.StartPathfinding(
            mazePlugin.Length,
            mazePlugin.Width,
            mazePlugin.StartPoint,
            mazePlugin.EndPoint,
            currentAlgorithm
        );

        isPathfindingRunning = true;
        visualizationCoroutine = StartCoroutine(PathfindingVisualizationProcess());
    }

    private IEnumerator PathfindingVisualizationProcess()
    {
        isVisualizing = true;
        Debug.Log("等待寻路数据...");

        int waitFrames = 0;
        while ((!pathfindingProvider.HasFrames() || waitFrames < 10) &&
               !pathfindingProvider.IsPathfindingFinished() && waitFrames < 200)
        {
            if (!isVisualizing) yield break;
            waitFrames++;
            yield return new WaitForSeconds(0.05f);
        }

        if (!pathfindingProvider.HasFrames())
        {
            Debug.LogWarning("未收到寻路数据，可视化结束");
            isVisualizing = false;
            yield break;
        }

        float frameInterval = 1f / framesPerSecond;
        float lastFrameTime = 0f;
        int processedFrames = 0;

        Debug.Log("开始处理寻路帧数据");

        //主可视化循环
        while (isVisualizing &&
               (pathfindingProvider.HasFrames() || !pathfindingProvider.IsPathfindingFinished()))
        {
            if (Time.time - lastFrameTime >= frameInterval)
            {
                int framesProcessedThisBatch = 0;
                bool hasValidDataThisBatch = false;

                while (pathfindingProvider.TryGetNextFrame(out int[] frameData) &&
                       framesProcessedThisBatch < batchProcessCount)
                {
                    if (!isVisualizing) break;
                    if (frameData != null && frameData.Length == mazePlugin.Length * mazePlugin.Width)
                    {
                        ProcessPathfindingFrame(frameData);
                        framesProcessedThisBatch++;
                        processedFrames++;
                        hasValidDataThisBatch = true;
                    }
                }

                if (hasValidDataThisBatch)
                {
                    ApplyBatchUpdates();
                }

                lastFrameTime = Time.time;

                if (processedFrames % 50 == 0)
                {
                    Debug.Log($"已处理 {processedFrames} 寻路帧，已显示格子: {visitedObjects.Count}");
                }
            }

            if (pathfindingProvider.IsPathfindingFinished() && !pathfindingProvider.HasFrames())
            {
                Debug.Log("检测到寻路结束信号，停止可视化循环");
                break;
            }

            yield return null;
        }

        Debug.Log($"寻路可视化完成，共处理 {processedFrames} 帧，总共显示 {visitedObjects.Count} 个已访问格子");
        isVisualizing = false;
        isPathfindingRunning = false;
    }

    //和迷宫生成的可视化逻辑基本相同
    private void ProcessPathfindingFrame(int[] frameData)
    {
        if (frameData == null) return;

        try
        {
            //转换为二维数组
            int[,] newState = new int[mazePlugin.Length, mazePlugin.Width];
            for (int x = 0; x < mazePlugin.Length; x++)
            {
                for (int y = 0; y < mazePlugin.Width; y++)
                {
                    int index = y * mazePlugin.Length + x;
                    newState[x, y] = frameData[index];
                }
            }

            //第一帧直接创建所有已访问格子
            if (isFirstPathfindingFrame)
            {
                for (int x = 0; x < mazePlugin.Length; x++)
                {
                    for (int y = 0; y < mazePlugin.Width; y++)
                    {
                        if (newState[x, y] == 1)
                        {
                            cellsToCreate.Add(new Vector2Int(x, y));
                        }
                    }
                }
                isFirstPathfindingFrame = false;
            }
            else
            {
                for (int x = 0; x < mazePlugin.Length; x++)
                {
                    for (int y = 0; y < mazePlugin.Width; y++)
                    {
                        Vector2Int pos = new Vector2Int(x, y);
                        bool wasVisited = currentPathfindingState[x, y] == 1;
                        bool isVisited = newState[x, y] == 1;

                        if (wasVisited && !isVisited)
                        {
                            cellsToRemove.Add(pos);
                            cellsToCreate.Remove(pos);
                        }
                        else if (!wasVisited && isVisited)
                        {
                            cellsToCreate.Add(pos);
                            cellsToRemove.Remove(pos);
                        }
                    }
                }
            }

            currentPathfindingState = newState;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"处理寻路帧时出错: {e.Message}");
        }
    }

    private void ApplyBatchUpdates()
    {
        foreach (var pos in cellsToRemove)
        {
            if (visitedObjects.ContainsKey(pos))
            {
                DestroyImmediate(visitedObjects[pos]);
                visitedObjects.Remove(pos);
            }
        }
        cellsToRemove.Clear();

        int createdThisFrame = 0;
        var cellsToCreateCopy = new List<Vector2Int>(cellsToCreate);
        foreach (var pos in cellsToCreateCopy)
        {
            if (!visitedObjects.ContainsKey(pos) && createdThisFrame < 20)
            {
                CreateVisitedCell(pos.x, pos.y);
                createdThisFrame++;
                cellsToCreate.Remove(pos);
            }
        }
    }

    private void CreateVisitedCell(int x, int y)
    {
        Vector2Int pos = new Vector2Int(x, y);
        if (visitedObjects.ContainsKey(pos))
            return;

        Vector3 position = new Vector3(x, 0.1f, y);
        GameObject cell = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cell.transform.position = position;
        cell.transform.localScale = new Vector3(0.9f, 0.1f, 0.9f);
        cell.name = $"Visited_{x}_{y}";

        Renderer cellRenderer = cell.GetComponent<Renderer>();
        if (visitedMaterial != null)
            cellRenderer.material = visitedMaterial;
        else
        {
            Material defaultMat = new Material(Shader.Find("Standard"));
            defaultMat.color = new Color(0, 0.5f, 1f, 0.7f);
            cellRenderer.material = defaultMat;
        }

        if (pathfindingParent != null)
            cell.transform.SetParent(pathfindingParent.transform);

        visitedObjects[pos] = cell;
        StartCoroutine(FadeInCell(cell));
    }

    private IEnumerator FadeInCell(GameObject cell)
    {
        if (cell == null) yield break;
        Renderer renderer = cell.GetComponent<Renderer>();
        Material mat = renderer.material;
        Color originalColor = mat.color;

        float duration = 0.3f;
        float elapsedTime = 0f;
        Color startColor = originalColor;
        startColor.a = 0f;
        mat.color = startColor;

        while (elapsedTime < duration && cell != null)
        {
            float progress = elapsedTime / duration;
            Color newColor = originalColor;
            newColor.a = progress * originalColor.a;
            mat.color = newColor;
            elapsedTime += Time.deltaTime;
            yield return null;
        }

        if (cell != null)
            mat.color = originalColor;
    }

    private void OnPathfindingComplete()
    {
        Debug.Log("寻路算法完成");
        isPathfindingRunning = false;
    }

    private void OnPathfindingError(string errorMessage)
    {
        Debug.LogError($"寻路过程中出错: {errorMessage}");
        isPathfindingRunning = false;
        isVisualizing = false;
    }

    public void StopPathfindingVisualization()
    {
        isVisualizing = false;
        isPathfindingRunning = false;
        if (visualizationCoroutine != null)
        {
            StopCoroutine(visualizationCoroutine);
            visualizationCoroutine = null;
        }
        if (pathfindingProvider != null)
            pathfindingProvider.StopPathfinding();

        CleanupVisualization();
    }

    public void SwitchAlgorithm(PathfindingDataProvider.PathfindingAlgorithm newAlgorithm)
    {
        currentAlgorithm = newAlgorithm;
        Debug.Log($"切换到 {newAlgorithm} 算法");
    }

    public PathfindingDataProvider.PathfindingAlgorithm GetCurrentAlgorithm() => currentAlgorithm;

    public void CleanupVisualizationImmediate()
    {
        isVisualizing = false;
        isPathfindingRunning = false;

        if (visualizationCoroutine != null)
        {
            StopCoroutine(visualizationCoroutine);
            visualizationCoroutine = null;
        }

        foreach (var cell in visitedObjects.Values)
        {
            if (cell != null)
                DestroyImmediate(cell);
        }
        visitedObjects.Clear();

        cellsToCreate.Clear();
        cellsToRemove.Clear();
        currentPathfindingState = null;
        isFirstPathfindingFrame = true;

        if (pathfindingParent != null)
        {
            DestroyImmediate(pathfindingParent);
            pathfindingParent = null;
        }

        CreatePathfindingParent();
    }

    public void CleanupVisualization()
    {
        if (visualizationCoroutine != null)
        {
            StopCoroutine(visualizationCoroutine);
            visualizationCoroutine = null;
        }

        foreach (var cell in visitedObjects.Values)
        {
            if (cell != null)
                DestroyImmediate(cell);
        }
        visitedObjects.Clear();

        cellsToCreate.Clear();
        cellsToRemove.Clear();
        currentPathfindingState = null;
        isFirstPathfindingFrame = true;
    }

    void OnDestroy()
    {
        StopPathfindingVisualization();
        CleanupVisualization();

        if (pathfindingParent != null)
        {
            Destroy(pathfindingParent);
            pathfindingParent = null;
        }

        var dispatcher = FindFirstObjectByType<UnityMainThreadDispatcher>();
        if (dispatcher != null)
        {
            Destroy(dispatcher.gameObject);
        }
    }
}