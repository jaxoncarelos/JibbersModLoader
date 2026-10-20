// Builds jibbers_fpv.bundle: one prefab ("JibbersFPVAudio") with three configured 2D AudioSources
// (Motor and Wind loops, Crash one-shot). The game strips AudioClip.Create and most AudioSource
// setters, so everything except pitch and volume is set up here. Run headless:
//   Unity.exe -batchmode -quit -projectPath mods/JibbersFPV/unity -executeMethod BuildFpv.Build
// after `cargo run --release --offline --example write_sounds` has written Assets/Audio/*.wav.
// The bundle lands in mods/JibbersFPV/build/.
using System.IO;
using UnityEditor;
using UnityEngine;

public static class BuildFpv
{
    const string Prefab = "Assets/Gen/JibbersFPVAudio.prefab";

    static AudioClip Clip(string name)
    {
        string path = $"Assets/Audio/{name}.wav";
        var importer = (AudioImporter)AssetImporter.GetAtPath(path);
        if (importer == null) throw new System.Exception($"{path} missing: run the write_sounds example first");
        var settings = importer.defaultSampleSettings;
        settings.loadType = AudioClipLoadType.DecompressOnLoad; // seamless loops, cheap pitch changes
        settings.compressionFormat = AudioCompressionFormat.PCM;
        importer.defaultSampleSettings = settings;
        importer.forceToMono = true;
        importer.loadInBackground = false;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
    }

    static void Source(GameObject root, string name, AudioClip clip, bool loop)
    {
        var child = new GameObject(name);
        child.transform.SetParent(root.transform, false);
        var s = child.AddComponent<AudioSource>();
        s.clip = clip;
        s.loop = loop;
        s.playOnAwake = false;
        s.spatialBlend = 0; // 2D: the pilot hears their own quad
        s.dopplerLevel = 0;
        s.volume = 0;
        s.priority = 64;
    }

    [MenuItem("Jibbers/Build FPV Bundle")]
    public static void Build()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Gen")) AssetDatabase.CreateFolder("Assets", "Gen");
        var root = new GameObject("JibbersFPVAudio");
        Source(root, "Motor", Clip("Motor"), true);
        Source(root, "Wind", Clip("Wind"), true);
        Source(root, "Crash", Clip("Crash"), false);
        PrefabUtility.SaveAsPrefabAsset(root, Prefab);
        Object.DestroyImmediate(root);
        AssetImporter.GetAtPath(Prefab).assetBundleName = "jibbers_fpv.bundle";
        string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../build"));
        Directory.CreateDirectory(output);
        BuildPipeline.BuildAssetBundles(output, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        Debug.Log("JibbersFPV bundle built in " + output);
    }
}
