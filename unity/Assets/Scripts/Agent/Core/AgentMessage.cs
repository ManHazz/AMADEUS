using System;

[Serializable]
public class AgentMessage
{
    public string type;
    public int sampleRate;
    public string pcm16;
    public string text;
    public string name;
    public float intensity;
    public string value;

    /// <summary>Parses a JSON message. Returns null on invalid input.</summary>
    public static AgentMessage Parse(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var m = UnityEngine.JsonUtility.FromJson<AgentMessage>(json);
            return (m == null || string.IsNullOrEmpty(m.type)) ? null : m;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Decodes base64 mono 16-bit little-endian PCM to floats in [-1,1].</summary>
    public static float[] DecodePcm16(string b64)
    {
        if (string.IsNullOrEmpty(b64)) return new float[0];
        byte[] b;
        try { b = Convert.FromBase64String(b64); }
        catch (FormatException) { return new float[0]; }
        int n = b.Length / 2;
        var f = new float[n];
        for (int i = 0; i < n; i++)
            f[i] = (short)(b[2 * i] | (b[2 * i + 1] << 8)) / 32768f;
        return f;
    }

    public static string BuildHello(string token)
    {
        return UnityEngine.JsonUtility.ToJson(new Hello { type = "hello", token = token ?? "" });
    }

    [Serializable] class Hello { public string type; public string token; }
    public string token;
}
