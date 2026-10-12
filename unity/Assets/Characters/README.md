# Characters

The avatar model is not included in this repository. You must supply your own VRM 1.0 file, for example one exported from VRoid Studio.

## Where to put it

Place the file at exactly this path inside the Unity project:

    Assets/Characters/amadeus.vrm

The file name (including the space and the lowercase letters) is hard-coded in `Assets/Editor/BuildRoom.cs` and `Assets/Editor/VrmTools.cs`. Either name your file that way, or change the path string in those two files. The avatar must be VRM 1.0 (it is imported with UniVRM 0.131.3 and driven through `Vrm10Instance` expressions). The expression and brow/eye tweaks in `VrmAvatar.cs` assume a VRoid-style face (for example the `Fcl_EYE_Fun` blend shape), so other models may import but look less expressive.

## Adding it to the scene

The committed scene `Assets/Scenes/Room.unity` was generated while the original VRM was present, so it holds a prefab instance that points at that model. Without the file Unity will show that instance as a missing prefab. After adding your VRM, regenerate the scene so it picks up your model:

    powershell -File tools/unity.ps1 method BuildRoom.Generate

(close the Unity Editor first, because the Editor cannot run twice on the same project), or call `BuildRoom.Generate` from your own Editor menu entry. `BuildRoom.Generate` rebuilds the room, instantiates the VRM from the path above under `AgentRoot`, and adds `VrmAvatar`.

## Without a VRM file

`BuildRoom.Generate` logs "VRM not found; using PlaceholderAvatar" and builds the room without an avatar prefab. At runtime `AgentController` looks for a `VrmAvatar` / `Vrm10Instance`; if none exists it logs a warning and adds `PlaceholderAvatar`, a simple stand-in body that implements the same expression interface, so the agent still runs (the stand-in is driven through the same expression interface). If you open the committed `Room.unity` as it is without the VRM, expect a missing-prefab entry for the old instance in the hierarchy; regenerate the scene as described above.

Do not commit VRM or VRoid files: check the license of your model first, and keep it out of public repositories unless the license allows redistribution. The root `.gitignore` already ignores `*.vrm` and `*.vroid`.
