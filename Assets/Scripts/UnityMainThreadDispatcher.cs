using UnityEngine;
using System.Collections;
using System.Collections.Concurrent;
using System;

//线程调度器
public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static UnityMainThreadDispatcher instance;
    private ConcurrentQueue<Action> actions = new ConcurrentQueue<Action>();

    public static UnityMainThreadDispatcher Instance
    {
        get
        {
            if (instance == null)
            {
                GameObject go = new GameObject("UnityMainThreadDispatcher");
                instance = go.AddComponent<UnityMainThreadDispatcher>();
                DontDestroyOnLoad(go);
            }
            return instance;
        }
    }

    public void Enqueue(Action action)
    {
        if (action != null)
        {
            actions.Enqueue(action);
        }
    }

    void Update()
    {
        // 在主线程执行所有排队的操作
        while (actions.TryDequeue(out Action action))
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogError($"在主线程执行操作时出错: {e.Message}");
            }
        }
    }

    void OnDestroy()
    {
        if (instance == this)
        {
            instance = null;
        }
    }
}