using System;
using System.Collections.Generic;
using UnityEngine;

public class UnityMainThreadDispatcher : MonoBehaviour
{
    static readonly Queue<Action> Q = new();
    static UnityMainThreadDispatcher _inst;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        if (_inst != null) return;
        var go = new GameObject("__MainThread__");
        GameObject.DontDestroyOnLoad(go);
        _inst = go.AddComponent<UnityMainThreadDispatcher>();
    }

    public static void Enqueue(Action a){ lock (Q) Q.Enqueue(a); }

    void Update()
    {
        while (true)
        {
            Action a = null;
            lock (Q) { if (Q.Count > 0) a = Q.Dequeue(); }
            if (a == null) break;
            try { a(); } catch (Exception e) { Debug.LogException(e); }
        }
    }
}
