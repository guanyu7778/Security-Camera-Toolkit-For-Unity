using System;
using Newtonsoft.Json;
using Unity.WebRTC;

namespace SecurityCameraToolkit.Runtime.WebRTC
{
    [Serializable]
    public sealed class SignalingMessage
    {
        public string type;
        public string sdp;
        public IceCandidatePayload candidate;

        public static SignalingMessage FromJson(string json)
            => string.IsNullOrWhiteSpace(json) ? null : JsonConvert.DeserializeObject<SignalingMessage>(json);

        public string ToJson(bool pretty = false)
            => JsonConvert.SerializeObject(this, pretty ? Formatting.Indented : Formatting.None);

        public static SignalingMessage CreateOffer(string sdp)
            => new SignalingMessage { type = "offer", sdp = sdp };

        public static SignalingMessage CreateAnswer(string sdp)
            => new SignalingMessage { type = "answer", sdp = sdp };

        public static SignalingMessage CreateIce(RTCIceCandidate candidate)
        {
            if (candidate == null || string.IsNullOrEmpty(candidate.Candidate))
                return null;

            return new SignalingMessage
            {
                type = "ice",
                candidate = new IceCandidatePayload
                {
                    candidate = candidate.Candidate,
                    sdpMid = candidate.SdpMid,
                    sdpMLineIndex = candidate.SdpMLineIndex.HasValue ? candidate.SdpMLineIndex.Value : 0
                }
            };
        }

        [Serializable]
        public sealed class IceCandidatePayload
        {
            public string candidate;
            public string sdpMid;
            public int sdpMLineIndex;

            public RTCIceCandidate ToCandidate()
            {
                var init = new RTCIceCandidateInit
                {
                    candidate = candidate,
                    sdpMid = string.IsNullOrEmpty(sdpMid) ? null : sdpMid,
                    sdpMLineIndex = sdpMLineIndex
                };
                return new RTCIceCandidate(init);
            }
        }
    }
}
