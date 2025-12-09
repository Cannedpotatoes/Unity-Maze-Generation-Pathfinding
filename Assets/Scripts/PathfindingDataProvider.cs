using UnityEngine;
using System;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Threading;
using System.Linq;

public class PathfindingDataProvider : MonoBehaviour
{
    public enum PathfindingAlgorithm
    {
        DFS,
        BFS
    }

    [DllImport("maze", CallingConvention = CallingConvention.Cdecl)]
    private static extern void dfs_search(int o_x, int o_y, int t_x, int t_y);
    
    [DllImport("maze", CallingConvention = CallingConvention.Cdecl)]
    private static extern void bfs_search(int o_x, int o_y, int t_x, int t_y);
    
    [DllImport("maze", CallingConvention = CallingConvention.Cdecl)]
    private static extern void get_frame_vct([Out] int[] array);
    
    [DllImport("maze", CallingConvention = CallingConvention.Cdecl)]
    private static extern void destory_frame();

    //数据队列和线程控制
    private ConcurrentQueue<int[]> frameQueue = new ConcurrentQueue<int[]>();
    private Thread pathfindingThread;
    private bool isPathfinding = false;
    private bool pathfindingComplete = false;
    private bool shouldStop = false;

    private int mazeLength;
    private int mazeWidth;
    private Vector2Int startPoint;
    private Vector2Int endPoint;
    private PathfindingAlgorithm algorithm;

    //态跟踪
    private int totalFramesProcessed = 0;
    private int lastVisitedCount = 0;
    private int sameVisitedCountFrames = 0;

    public event System.Action OnPathfindingComplete;
    public event System.Action<string> OnPathfindingError;

    public void StartPathfinding(int length, int width, Vector2Int start, Vector2Int end, PathfindingAlgorithm algo)
    {
        if (isPathfinding)
        {
            Debug.Log("正在寻路中，先停止之前的寻路");
            StopPathfinding();
            Thread.Sleep(100);
        }

        //重置状态
        while (frameQueue.TryDequeue(out _)) { }
        isPathfinding = true;
        pathfindingComplete = false;
        shouldStop = false;
        totalFramesProcessed = 0;
        lastVisitedCount = 0;
        sameVisitedCountFrames = 0;

        mazeLength = length;
        mazeWidth = width;
        startPoint = start;
        endPoint = end;
        algorithm = algo;

        Debug.Log($"开始{algorithm}寻路: 起点({start.x},{start.y}) -> 终点({end.x},{end.y})");

        //创建并启动寻路线程
        pathfindingThread = new Thread(() => GeneratePathfindingFrames());
        pathfindingThread.IsBackground = true;
        pathfindingThread.Name = "PathfindingThread";
        pathfindingThread.Start();
    }

    //寻路线程的主方法，与迷宫生成相同的完成检测逻辑
    private void GeneratePathfindingFrames()
    {
        try
        {
            Debug.Log("寻路线程开始");
            
            Debug.Log($"调用{algorithm}寻路算法...");
            
            if (algorithm == PathfindingAlgorithm.DFS)
            {
                dfs_search(startPoint.x, startPoint.y, endPoint.x, endPoint.y);
            }
            else
            {
                bfs_search(startPoint.x, startPoint.y, endPoint.x, endPoint.y);
            }
            
            Debug.Log("寻路算法调用完成");

            int arraySize = mazeLength * mazeWidth;
            int[] frameData = new int[arraySize];
            

            int maxFrames = mazeLength * mazeWidth * 10;
            int processedFrames = 0;
            int consecutiveEmptyFrames = 0;
            const int maxConsecutiveEmptyFrames = 200;
            
            // 与迷宫生成相同的稳定检测
            const int maxStableFrames = 30;
            int expectedVisitedCells = (int)(mazeLength * mazeWidth * 0.8f); //预计最多访问80%的格子

            bool hasReceivedAnyData = false;
            bool isFirstFrame = true;

            while (isPathfinding && !shouldStop && processedFrames < maxFrames)
            {
                if (shouldStop) break;

                Array.Clear(frameData, 0, frameData.Length);
                get_frame_vct(frameData);
                
                bool hasValidData = false;
                int currentVisitedCount = 0;
                
                try
                {
                    currentVisitedCount = frameData.Count(x => x == 1);
                    hasValidData = currentVisitedCount > 0;
                    
                    if (!hasReceivedAnyData && hasValidData)
                    {
                        Debug.Log($"收到第一帧有效数据 - 已访问格子: {currentVisitedCount}");
                        hasReceivedAnyData = true;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"帧数据检查异常: {e.Message}");
                    hasValidData = false;
                }
                
                if (hasValidData)
                {
                    int[] frameCopy = new int[arraySize];
                    try
                    {
                        Buffer.BlockCopy(frameData, 0, frameCopy, 0, arraySize * sizeof(int));
                        frameQueue.Enqueue(frameCopy);
                        
                        consecutiveEmptyFrames = 0;
                        processedFrames++;
                        totalFramesProcessed = processedFrames;
                        
                        //完成检测，与迷宫生成相同的逻辑
                        if (isFirstFrame)
                        {
                            isFirstFrame = false;
                        }
                        else
                        {
                            if (currentVisitedCount == lastVisitedCount)
                            {
                                sameVisitedCountFrames++;
                                
                                float completionRatio = (float)currentVisitedCount / expectedVisitedCells;
                                if (completionRatio > 0.1f && sameVisitedCountFrames > maxStableFrames)
                                {
                                    Debug.Log($"寻路稳定结束: 已访问格子 {currentVisitedCount} 稳定 {sameVisitedCountFrames} 帧 (完成度 {completionRatio * 100f:F1}%)");
                                    break;
                                }
                            }
                            else
                            {
                                sameVisitedCountFrames = 0;
                                lastVisitedCount = currentVisitedCount;
                            }
                        }
                        
                        if (processedFrames % 20 == 0)
                        {
                            Debug.Log($"已处理 {processedFrames} 寻路帧，已访问格子: {currentVisitedCount}");
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"处理寻路帧数据时出错: {e.Message}");
                    }
                }
                else
                {
                    consecutiveEmptyFrames++;
                    if (consecutiveEmptyFrames >= maxConsecutiveEmptyFrames)
                    {
                        Debug.Log($"寻路完成检测: 连续 {consecutiveEmptyFrames} 帧无有效数据，已处理 {processedFrames} 帧");
                        break;
                    }
                    else if (consecutiveEmptyFrames % 30 == 0)
                    {
                        Debug.Log($"连续 {consecutiveEmptyFrames} 帧无有效数据...");
                    }
                }
                
                try
                {
                    destory_frame();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"销毁帧时出错: {e.Message}");
                }
                
                Thread.Sleep(5);
            }
            
            pathfindingComplete = true;
            Debug.Log($"寻路完成! 总共处理 {processedFrames} 帧，收到有效数据: {hasReceivedAnyData}");
            
            if (hasReceivedAnyData)
            {
                UnityMainThreadDispatcher.Instance?.Enqueue(() => {
                    try
                    {
                        OnPathfindingComplete?.Invoke();
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"调用寻路完成事件时出错: {e.Message}");
                    }
                });
            }
            else
            {
                Debug.LogError("寻路算法没有生成任何有效数据！");
                
                UnityMainThreadDispatcher.Instance?.Enqueue(() => {
                    try
                    {
                        OnPathfindingError?.Invoke("寻路算法没有生成有效数据");
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"调用寻路错误事件时出错: {e.Message}");
                    }
                });
            }
        }
        catch (ThreadAbortException)
        {
            Debug.Log("寻路线程被中止");
        }
        catch (Exception e)
        {
            Debug.LogError($"寻路线程异常: {e.Message}\n{e.StackTrace}");
            pathfindingComplete = true;
            
            UnityMainThreadDispatcher.Instance?.Enqueue(() => {
                try
                {
                    OnPathfindingError?.Invoke(e.Message);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"调用寻路错误事件时出错: {ex.Message}");
                }
            });
        }
        finally
        {
            isPathfinding = false;
            Debug.Log("寻路线程结束");
        }
    }

    public bool TryGetNextFrame(out int[] frameData)
    {
        if (frameQueue.TryDequeue(out frameData))
        {
            return true;
        }
        frameData = null;
        return false;
    }

    public bool HasFrames()
    {
        return !frameQueue.IsEmpty;
    }

    public bool IsPathfindingFinished()
    {
        return pathfindingComplete && frameQueue.IsEmpty;
    }

    public bool IsPathfinding
    {
        get { return isPathfinding; }
    }

    public int TotalFramesProcessed
    {
        get { return totalFramesProcessed; }
    }

    public bool IsDllAvailable()
    {
        return !isPathfinding;
    }

    public void StopPathfinding()
    {
        Debug.Log("停止寻路...");
        shouldStop = true;
        isPathfinding = false;
        pathfindingComplete = true;
        
        if (pathfindingThread != null && pathfindingThread.IsAlive)
        {
            if (!pathfindingThread.Join(1000))
            {
                Debug.LogWarning("寻路线程未正常停止，尝试中止");
                try
                {
                    pathfindingThread.Abort();
                }
                catch (Exception e)
                {
                    Debug.LogError($"中止寻路线程时出错: {e.Message}");
                }
            }
            pathfindingThread = null;
        }
        
        while (frameQueue.TryDequeue(out _)) { }
        
        Debug.Log("寻路已完全停止");
    }

    void OnDestroy()
    {
        StopPathfinding();
    }
}