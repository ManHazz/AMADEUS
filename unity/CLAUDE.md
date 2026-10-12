# Unity project
Unity 6000.6.5f1. Editor cannot run twice on same project: close Editor before CLI.
Verify every code change: `powershell -File tools/unity.ps1 compile` (must show no `error CS`).
Tests: `tools/unity.ps1 test-edit` / `test-play`. Build: `tools/unity.ps1 build`.
Logs in `Logs/cli-*.log`. Never edit `Library/`, `*.meta` by hand. Keep scripts in `Assets/Scripts/`.
Scenes/prefabs = YAML; prefer creating them via Editor scripts (`Assets/Editor/`) + `-executeMethod`.
Subtitles: server EVENT kinds `segment`/`segment_tr` -> AgentHttpClient.OnSegment/OnSegmentTranslation -> SubtitleView (IMGUI, built by AgentController at runtime; no ugui/TMP package in project).
Furigana = per-token ruby row above base row (SubtitleView layout, wraps by measured width). Font: Assets/Resources/Fonts/NotoSansJP-Regular.otf (OFL), OS font fallback.
Tips: segment with lang=en,spoken=false (ENGLISH_FREE) = silent tip line (small amber italic) above the JA line in the same panel; kept until speech ends, tip-only turn shown 4-6 s; never replaces JA/tr.
F9 (editor/dev builds) shows a sample JA segment, F10 a sample tip. Parser + tests: Core/SegmentEvent.cs, Tests/Editor/SegmentEventTests.cs.
Avatar/lip sync: LipSyncDriver reads 10 ms RMS windows pushed by AudioStreamPlayer.OnRead (not GetOutputData); VrmAvatar sets VRM1 expressions every Update (vowel cycling, micro-smile, head nod). Emotion: AgentController classifies segment text + segment_tr English + meta reply (KeywordEmotionClassifier, JA+EN cues, floor 0.6) -> EmotionDriver (held until speech ends + 2 s).
Camera: CameraRig on Main Camera; keys 1 body / 2 face (default) / 3 bust, Tab cycles. F2 = debug panel (emotion buttons, mouth slider, camera) + HUD debug line. TranscriptHud (top-left) shows META.user.
Subtitles stack the whole turn (max 4 lines); playing line highlighted via SegmentAudioTracker (segment ids vs audio chunk positions). Cleared on next request/recording. F9 = 3-line sample.
Verify with graphics: `tools/unity.ps1 test-play-gfx` (runs Tests/PlayMode/RoomVerifyTests -> Scripts/Agent/VerifyHarness; writes Logs/face_open|closed|happy.png, hud.png, subtitles_stack.png; opens a window, not batchmode). test-play (nographics) skips it.
Gfx harness also writes face_happy_speaking/idle, face_angry_speaking, face_with_subs (4-line stack), furigana.png. Emotions: VrmAvatar caps whole-face Fcl_ALL_* presets while speaking and drives eye/brow raw morphs (Fcl_EYE_Fun/BRW_*) uncapped; dump via VrmTools.DumpExpressions/DumpBlendShapes. Ruby wrap maths: Core/RubyLayout.cs (tested).
