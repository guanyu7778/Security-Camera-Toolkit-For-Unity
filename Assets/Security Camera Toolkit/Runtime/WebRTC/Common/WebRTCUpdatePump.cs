using UnityEngine;
using Unity.WebRTC;

namespace SecurityCameraToolkit.Runtime.WebRTC
{
    internal sealed class WebRTCUpdatePump : MonoBehaviour
    {
        static WebRTCUpdatePump _instance;
        Coroutine _updateCoroutine;
        int _refCount;

        internal static WebRTCUpdatePump Instance
        {
            get
            {
                if (_instance != null)
                    return _instance;

                var go = new GameObject("__WebRTCUpdatePump__");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<WebRTCUpdatePump>();
                return _instance;
            }
        }

        internal void Retain()
        {
            _refCount++;
            if (_updateCoroutine == null)
            {
                _updateCoroutine = StartCoroutine(global::Unity.WebRTC.WebRTC.Update());
            }
        }

        internal void Release()
        {
            if (_refCount <= 0)
                return;

            _refCount--;
            if (_refCount == 0 && _updateCoroutine != null)
            {
                StopCoroutine(_updateCoroutine);
                _updateCoroutine = null;
            }
        }
    }
}
