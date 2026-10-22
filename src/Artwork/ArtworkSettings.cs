using System;
using System.Runtime.Serialization;

namespace SinmaiAlpha.Artwork
{
    [DataContract]
    public sealed class ArtworkSettings
    {
        // Opt in so existing installations retain their current asset providers.
        [DataMember] public bool Enabled = false;
        [DataMember] public bool EnableJackets = true;
        [DataMember] public bool EnableFrames = true;
        [DataMember] public bool EnableImages = true;
        [DataMember] public bool EnableGif = true;
        [DataMember] public bool EnableVideo = true;
        [DataMember] public string Directory = "LocalAssets";

        [OnDeserializing] private void Defaults(StreamingContext context)
        {
            Enabled = false;
            EnableJackets = EnableFrames = EnableImages = EnableGif = EnableVideo = true;
            Directory = "LocalAssets";
        }
        [OnDeserialized] private void Validate(StreamingContext context)
        {
            if (string.IsNullOrWhiteSpace(Directory)) Directory = "LocalAssets";
        }
    }
}
