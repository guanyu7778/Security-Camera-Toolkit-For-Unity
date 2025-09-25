using System;
using UnityEngine;

namespace SecurityCameraToolkit.Runtime.WebRTC
{
    [Serializable]
    public class CameraPoseMessage
    {
        public float[] position;
        public float[] rotation;
        public string calibration;
        public string timestamp;

        public bool HasPosition => position != null && position.Length >= 3;
        public bool HasRotation => rotation != null && rotation.Length >= 4;

        public bool TryGetPosition(out Vector3 value)
        {
            if (HasPosition)
            {
                value = new Vector3(position[0], position[1], position[2]);
                return true;
            }

            value = default;
            return false;
        }

        public bool TryGetRotation(out Quaternion value)
        {
            if (HasRotation)
            {
                var q = new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]);
                var magSq = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
                if (magSq > 1e-8f)
                {
                    var invMag = 1f / Mathf.Sqrt(magSq);
                    q.x *= invMag;
                    q.y *= invMag;
                    q.z *= invMag;
                    q.w *= invMag;
                }
                else
                {
                    value = Quaternion.identity;
                    return false;
                }

                value = q;
                return true;
            }

            value = Quaternion.identity;
            return false;
        }

        public bool TryGetPose(out Vector3 positionValue, out Quaternion rotationValue)
        {
            var hasPos = TryGetPosition(out positionValue);
            var hasRot = TryGetRotation(out rotationValue);
            return hasPos && hasRot;
        }

        public void EnsureConsistency()
        {
            position = EnsureArrayLength(position, 3);
            rotation = EnsureArrayLength(rotation, 4);

            if (rotation != null)
            {
                var magSq = rotation[0] * rotation[0] + rotation[1] * rotation[1] + rotation[2] * rotation[2] + rotation[3] * rotation[3];
                if (magSq > 1e-8f)
                {
                    var invMag = 1f / Mathf.Sqrt(magSq);
                    rotation[0] *= invMag;
                    rotation[1] *= invMag;
                    rotation[2] *= invMag;
                    rotation[3] *= invMag;
                }
                else
                {
                    rotation = null;
                }
            }
        }

        public string ToJson(bool prettyPrint = false)
        {
            return JsonUtility.ToJson(this, prettyPrint);
        }

        public static CameraPoseMessage FromTransform(Transform transform, string calibrationId = null, string timestampValue = null)
        {
            if (transform == null)
                return null;

            var pose = new CameraPoseMessage
            {
                position = new[] { transform.position.x, transform.position.y, transform.position.z },
                rotation = new[] { transform.rotation.x, transform.rotation.y, transform.rotation.z, transform.rotation.w },
                calibration = calibrationId,
                timestamp = string.IsNullOrEmpty(timestampValue) ? DateTime.UtcNow.ToString("o") : timestampValue
            };

            pose.EnsureConsistency();
            return pose;
        }

        static float[] EnsureArrayLength(float[] source, int requiredLength)
        {
            if (source == null || source.Length < requiredLength)
                return null;
            if (source.Length == requiredLength)
                return source;

            var trimmed = new float[requiredLength];
            Array.Copy(source, trimmed, requiredLength);
            return trimmed;
        }
    }
}
