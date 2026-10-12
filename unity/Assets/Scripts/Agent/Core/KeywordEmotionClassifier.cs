using System;

/// <summary>Cheap keyword/punctuation heuristic: reply text -> emotion name + intensity.</summary>
public static class KeywordEmotionClassifier
{
    public struct Result
    {
        public string Name; public float Intensity;
        public Result(string n, float i) { Name = n; Intensity = i; }
    }

    public const float MinIntensity = 0.6f; // any non-neutral emotion must be clearly visible

    static readonly string[] Names = { "happy", "sad", "angry", "surprised", "embarrassed" };
    static readonly string[][] Words =
    {
        // happy
        new[] { "happy", "glad", "great", "awesome", "wonderful", "love", "haha", "lol", "yay", "fun", "delight", "excited", "thank", "nice", "congrat",
                "good job", "well done", "perfect", "excellent", "correct", "right!", "beautiful", "welcome", "pleased", "cheers",
                "ありがとう", "ありがと", "すごい", "凄い", "うれしい", "嬉しい", "楽しい", "たのしい", "よかった", "良かった", "最高", "すてき", "素敵", "やった", "上手", "じょうず",
                "えらい", "正解", "せいかい", "おめでとう", "好き", "だいすき", "わーい", "ふふ", "あはは", "はは", "笑", "おいしい", "かわいい", "可愛い", "いいね", "完璧", "その通り" },
        // sad
        new[] { "sad", "sorry", "unfortunately", "miss", "cry", "regret", "lonely", "depress", "sadly", "grief", "hurt", "apolog", "pity", "too bad",
                "悲しい", "かなしい", "残念", "ざんねん", "ごめん", "すみません", "申し訳", "さみしい", "寂しい", "つらい", "辛い", "泣", "しくしく", "がっかり" },
        // angry
        new[] { "angry", "furious", "annoy", "hate", "mad ", "outrage", "unacceptable", "ridiculous", "irritat", "damn", "wrong", "no, no", "stop it", "not like that",
                "だめ", "ダメ", "駄目", "違う", "ちがう", "ばか", "バカ", "馬鹿", "怒", "むかつく", "ムカつく", "いい加減", "やめて", "許さ", "もう！" },
        // surprised
        new[] { "wow", "whoa", "surpris", "unexpected", "no way", "incredible", "amazing", "unbelievable", "really?", "oh!", "what?!", "seriously", "huh", "oh my", "you did what",
                "えっ", "え？", "えー", "まさか", "びっくり", "驚", "うそ", "嘘", "まじ", "マジ", "本当に", "ほんとう", "ほんと？", "ええっ", "なんと", "信じられ" },
        // embarrassed
        new[] { "embarrass", "blush", "awkward", "um,", "umm", "uh,", "shy", "oops", "ahem", "ashamed", "flatter", "not like i", "it's not like", "whatever",
                "恥ずかしい", "はずかしい", "別に", "べつに", "照れ", "てれ", "えっと", "ええと", "あの…", "あの、", "そ、そんな", "もう…", "ち、違", "///" },
    };

    /// <summary>Classifies several texts (e.g. JA reply + EN translation); strongest non-neutral wins.</summary>
    public static Result ClassifyAny(params string[] texts)
    {
        var best = new Result("neutral", 0f);
        if (texts == null) return best;
        foreach (var t in texts)
        {
            var r = Classify(t);
            if (r.Intensity > best.Intensity) best = r;
        }
        return best;
    }

    public static Result Classify(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new Result("neutral", 0f);
        string t = text.ToLowerInvariant();
        int ex = 0;
        foreach (char c in t) if (c == '!' || c == '！') ex++;
        bool ellipsis = t.Contains("...") || t.Contains("…") || t.Contains("、、");

        var score = new float[Names.Length];
        var hit = new bool[Names.Length];
        for (int e = 0; e < Names.Length; e++)
            foreach (var w in Words[e])
                if (t.Contains(w)) { score[e] += 1f; hit[e] = true; }

        // punctuation only nudges emotions that already have a keyword hit
        if (ex > 0) { score[0] += 0.5f; score[3] += 0.5f; score[2] += 0.25f; }
        if (t.Contains("?!") || t.Contains("!?") || t.Contains("？！") || t.Contains("！？") || t.Contains("?！") || t.Contains("!？")) score[3] += 1f;
        if (ellipsis) { score[4] += 0.5f; score[1] += 0.25f; }

        int best = -1; float bs = 0f;
        for (int e = 0; e < score.Length; e++)
            if (hit[e] && score[e] > bs) { bs = score[e]; best = e; }
        if (best < 0) return new Result("neutral", 0f);

        float intensity = 0.6f + 0.15f * (bs - 1f) + 0.1f * Math.Min(ex, 3);
        if (intensity < MinIntensity) intensity = MinIntensity;
        if (intensity > 1f) intensity = 1f;
        return new Result(Names[best], intensity);
    }
}
