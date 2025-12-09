using UnityEngine;
using System;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Threading;
using System.Linq;

public class MazeDataProvider : MonoBehaviour
{
    [DllImport("maze", CallingConvention = CallingConvention.Cdecl)]
    private static extern void creat_maze(int len, int wid, int blen);

    [DllImport("maze", CallingConvention = CallingConvention.Cdecl)]
    private static extern void get_frame_vct([Out] int[] array);

    [DllImport("maze", CallingConvention = CallingConvention.Cdecl)]
    private static extern void destory_frame();

    [DllImport("maze", CallingConvention = CallingConvention.Cdecl)]
    private static extern void destory_maze();

    [DllImport("maze", CallingConvention = CallingConvention.Cdecl)]
    private static extern void dfs_search(int o_x, int o_y, int t_x, int t_y);

    [DllImport("maze", CallingConvention = CallingConvention.Cdecl)]
    private static extern void bfs_search(int o_x, int o_y, int t_x, int t_y);

    private ConcurrentQueue<int[]> frameQueue = new ConcurrentQueue<int[]>(); //线程安全队列
    private Thread generationThread; //线程
    private bool isGenerating = false; 
    private bool generationComplete = false;
    private bool shouldStop = false;

    // 生成统计
    private int totalFramesProcessed = 0; //总处理帧数
    private int consecutiveEmptyFrames = 0; //连续空帧 
    private int lastFrameWallCount = 0; //上一帧的墙数
    private int sameWallCountFrames = 0; //墙数量恒定的帧数量
    private int expectedTotalWalls = 0; //预期的墙数量
    private bool hasReasonableWallCount = false; // 添加缺失的变量 

    // 同步控制
    private object threadLock = new object(); //线程同步锁

    public event System.Action OnGenerationComplete;

    private bool dllInitialized = false;

    public void StartGeneration(int length, int width, int blockLength)
    {
        if (isGenerating)
        {
            Debug.Log("正在生成中，停止之前的生成");
            StopGeneration();
            Thread.Sleep(200);
        }

        try
        {
            UnityMainThreadDispatcher.Instance.Enqueue(() =>
            {
                try
                {
                    destory_maze();
                    dllInitialized = false;
                    Debug.Log("已清空上次的DLL迷宫实例");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"清理旧迷宫失败: {e.Message}");
                }
            });
        }
        catch { }

        //重置状态
        lock (threadLock)
        {
            while (frameQueue.TryDequeue(out _)) { } //不断出队
            isGenerating = true;
            generationComplete = false;
            shouldStop = false;
            totalFramesProcessed = 0;
            consecutiveEmptyFrames = 0;
            lastFrameWallCount = 0;
            sameWallCountFrames = 0;
            hasReasonableWallCount = false;

            //估算预期的墙数量（大约50%的格子会是墙）
            expectedTotalWalls = (int)(length * width * 0.5f);
        }

        Debug.Log($"开始迷宫生成: {length}x{width}, 预期墙数量: {expectedTotalWalls}");

        generationThread = new Thread(() => GenerateMazeFrames(length, width, blockLength));
        generationThread.IsBackground = true; //设置为后台线程
        generationThread.Name = "MazeGenerationThread";
        generationThread.Start();
    }

    private void GenerateMazeFrames(int length, int width, int blockLength)
    {
        try
        {
            Debug.Log("迷宫生成线程开始");

            bool initSuccess = false;
            UnityMainThreadDispatcher.Instance.Enqueue(() => {
                try
                {
                    Debug.Log("在主线程初始化DLL...");
                    creat_maze(length, width, blockLength);
                    initSuccess = true;
                    dllInitialized = true;  
                    Debug.Log("DLL初始化完成");
                }
                catch (Exception e)
                {
                    Debug.LogError($"DLL初始化失败: {e.Message}");
                    dllInitialized = false;
                    initSuccess = false;
                }
            });

            //等待DLL初始化完成
            int waitCount = 0;
            while (!initSuccess && waitCount < 50)
            {
                Thread.Sleep(50);
                waitCount++;
            }

            if (!initSuccess)
            {
                Debug.LogError("DLL初始化超时或失败");
                return;
            }

            int arraySize = length * width;
            int[] frameData = new int[arraySize];

            int mazeSize = length * width;
            int maxFrames = Mathf.Clamp(mazeSize * 10, 5000, 30000); //最大帧数
            int maxConsecutiveEmptyFrames = Mathf.Clamp(mazeSize / 20, 100, 500); //空帧容忍
            int maxSameWallCountFrames = Mathf.Clamp(mazeSize / 30, 50, 300); //稳定检测

            //墙数量预期
            expectedTotalWalls = (int)(mazeSize * 0.6f);
            int reasonableWallCountThreshold = (int)(expectedTotalWalls * 0.5f);

            Debug.Log($"迷宫生成参数 - 最大帧数: {maxFrames}, 空帧容忍: {maxConsecutiveEmptyFrames}, 稳定阈值: {maxSameWallCountFrames}");

            while (isGenerating && !shouldStop && totalFramesProcessed < maxFrames)
            {
                if (shouldStop) break;

                Array.Clear(frameData, 0, frameData.Length);
                get_frame_vct(frameData);

                bool hasValidData = false;
                int currentWallCount = 0;

                try
                {
                    currentWallCount = frameData.Count(x => x == 2);
                    hasValidData = currentWallCount > 0 && frameData.Any(x => x != 0);

                    if (currentWallCount >= reasonableWallCountThreshold)
                    {
                        hasReasonableWallCount = true; 
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

                        if (currentWallCount == lastFrameWallCount)
                        {
                            sameWallCountFrames++;

                            //达到合理墙数量且稳定很长时间才认为完成
                            if (hasReasonableWallCount && sameWallCountFrames >= maxSameWallCountFrames)
                            {
                                //确保墙数量接近预期
                                float finalRatio = (float)currentWallCount / expectedTotalWalls;
                                if (finalRatio > 0.5f) //至少达到预期的50%
                                {
                                    Debug.Log($"生成完成检测: 墙数量 {currentWallCount} (完成度 {finalRatio * 100f:F1}%) 稳定 {sameWallCountFrames} 帧");
                                    break;
                                }
                                else
                                {
                                    Debug.Log($"墙数量稳定但不足: {currentWallCount} (完成度 {finalRatio * 100f:F1}%)，继续生成");
                                    sameWallCountFrames = maxSameWallCountFrames / 2; //重置一半计数继续等待
                                }
                            }
                        }
                        else
                        {
                            sameWallCountFrames = 0;
                            lastFrameWallCount = currentWallCount;
                        }

                        totalFramesProcessed++;

                        if (totalFramesProcessed % 100 == 0)
                        {
                            float completionPercent = ((float)currentWallCount / expectedTotalWalls * 100f);
                            Debug.Log($"已生成 {totalFramesProcessed} 帧，当前墙数量: {currentWallCount} ({completionPercent:F1}%)，稳定帧: {sameWallCountFrames}");
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"处理帧数据时出错: {e.Message}");
                    }
                }
                else
                {
                    consecutiveEmptyFrames++; 
                    if (consecutiveEmptyFrames >= maxConsecutiveEmptyFrames) 
                    {
                        //在墙数量达到合理水平后才认为空帧是完成信号
                        if (totalFramesProcessed > 100 && hasReasonableWallCount) 
                        {
                            Debug.Log($"生成完成检测: 连续 {consecutiveEmptyFrames} 帧无有效数据，已处理 {totalFramesProcessed} 有效帧"); 
                            break;
                        }
                        else if (consecutiveEmptyFrames >= maxConsecutiveEmptyFrames * 2) 
                        {
                            //如果空帧持续过久，强制结束
                            Debug.LogWarning($"长时间无数据，强制结束生成。有效帧: {totalFramesProcessed}，墙数量: {lastFrameWallCount}");
                            break;
                        }
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

                Thread.Sleep(10);
            }

            int finalWallCount = lastFrameWallCount;
            float finalCompletionRatio = (float)finalWallCount / expectedTotalWalls;

            if (finalCompletionRatio < 0.4f)
            {
                Debug.LogWarning($"最终墙数量可能不足: {finalWallCount} (完成度 {finalCompletionRatio * 100f:F1}%)");
            }

            if (shouldStop)
            {
                CleanupDllResources();
            }

            generationComplete = true;
            Debug.Log($"迷宫数据生成完成! 总共处理 {totalFramesProcessed} 帧，最终墙数量: {finalWallCount}，完成度: {finalCompletionRatio * 100f:F1}%");

            UnityMainThreadDispatcher.Instance?.Enqueue(() => {
                try
                {
                    OnGenerationComplete?.Invoke();
                }
                catch (Exception e)
                {
                    Debug.LogError($"调用完成事件时出错: {e.Message}");
                }
            });
        }
        catch (ThreadAbortException)
        {
            Debug.Log("迷宫生成线程被中止");
        }
        catch (Exception e)
        {
            Debug.LogError($"迷宫生成线程异常: {e.Message}\n{e.StackTrace}");
            generationComplete = true;
            UnityMainThreadDispatcher.Instance?.Enqueue(() => {
                try
                {
                    OnGenerationComplete?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"调用完成事件时出错: {ex.Message}");
                    generationComplete = true;
                    CleanupDllResources();
                }
            });
        }
        finally
        {
            isGenerating = false;
            Debug.Log("迷宫生成线程结束");
        }
    }

    private void CleanupDllResources()
    {
        try
        {
            if(!dllInitialized) return;
            UnityMainThreadDispatcher.Instance.Enqueue(() => {
                try
                {
                    destory_maze();
                    dllInitialized = false;
                    Debug.Log("DLL资源已清理");
                }
                catch (Exception e)
                {
                    Debug.LogError($"清理DLL资源时出错: {e.Message}");
                }
            });
        }
        catch (Exception e)
        {
            Debug.LogError($"安排DLL资源清理时出错: {e.Message}");
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

    public bool IsGenerationFinished()
    {
        return generationComplete && frameQueue.IsEmpty;
    }

    public bool IsGenerating
    {
        get { return isGenerating; }
    }

    public void StopGeneration()
    {
        Debug.Log("停止迷宫生成...");
        shouldStop = true;
        isGenerating = false;
        generationComplete = true;

        if (generationThread != null && generationThread.IsAlive)
        {
            if (!generationThread.Join(2000))
            {
                Debug.LogWarning("生成线程未正常停止，尝试中止");
                try
                {
                    generationThread.Abort();
                }
                catch (Exception e)
                {
                    Debug.LogError($"中止线程时出错: {e.Message}");
                }
            }
            generationThread = null;
        }

        CleanupDllResources();

        while (frameQueue.TryDequeue(out _)) { }

        Debug.Log("迷宫生成已完全停止");
    }

    public void CleanupMazeResources()
    {
        CleanupDllResources();
    }

    void OnDestroy()
    {
        StopGeneration();
    }
}