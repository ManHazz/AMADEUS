public interface IAvatarExpressions
{
    void SetEmotion(string name, float intensity);
    void SetMouth(float open);
    void SetBlink(float amount);
    /// <summary>Speech energy 0..1 (smoothed). Drives micro-expressions, head nods and jaw while talking.</summary>
    void SetSpeaking(float energy);
}
