using System;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object=UnityEngine.Object;

public static class NightRenderingTests
{
    public static void Run() { CheckNightSky();CheckShadowProxy(); }

    static Color[] ReadPixels(RenderTexture target)
    {
        var previous=RenderTexture.active;RenderTexture.active=target;
        var image=new Texture2D(target.width,target.height,TextureFormat.RGBAFloat,false,true);
        image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);image.Apply();
        var pixels=image.GetPixels();Object.DestroyImmediate(image);RenderTexture.active=previous;return pixels;
    }

    static void CheckNightSky()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        GraphicsSettings.defaultRenderPipeline=null;QualitySettings.renderPipeline=null;
        var camera=new GameObject("NightSkyCamera").AddComponent<Camera>();
        camera.clearFlags=CameraClearFlags.Skybox;
        camera.transform.rotation=Quaternion.LookRotation(new Vector3(.3f,.6f,.4f));
        var target=new RenderTexture(128,128,24,RenderTextureFormat.ARGBHalf);target.Create();camera.targetTexture=target;
        var sky=new Material(Shader.Find("AlpineVision/AlpineSky"));RenderSettings.skybox=sky;
        sky.SetVector("_AV_Clouds",new Vector4(.6f,0,0,0));
        sky.SetVector("_AV_Sun",new Vector4(.3f,.6f,.4f,0));sky.SetVector("_AV_Night",new Vector4(1,0,0,0));
        foreach(var moonlit in new[]{true,false})
        {
            sky.SetColor("_AV_Zenith",moonlit?new Color(.008f,.016f,.038f):new Color(.006f,.012f,.027f));
            sky.SetColor("_AV_Horizon",moonlit?new Color(.016f,.025f,.048f):new Color(.025f,.030f,.046f));
            camera.Render();float peak=0;
            foreach(var colour in ReadPixels(target))peak=Mathf.Max(peak,colour.maxColorComponent);
            if(peak>.1f || peak<.001f)throw new Exception("Night sky/clouds are daylight-bright or black: "+peak);
        }
        RenderSettings.skybox=null;camera.targetTexture=null;target.Release();
        Object.DestroyImmediate(target);Object.DestroyImmediate(sky);Object.DestroyImmediate(camera.gameObject);
        Debug.Log("PASS both night skies stay dark with cloud cover and no extra sun disc.");
    }

    static void CheckShadowProxy()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        var rendererData=ScriptableObject.CreateInstance<UniversalRendererData>();
        var pipeline=UniversalRenderPipelineAsset.Create(rendererData);
        var pipelineSettings=new UnityEditor.SerializedObject(pipeline);
        pipelineSettings.FindProperty("m_AdditionalLightShadowsSupported").boolValue=true;
        pipelineSettings.ApplyModifiedPropertiesWithoutUndo();pipeline.shadowDistance=120;
        GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;
        typeof(RenderPipelineManager).GetMethod("TryPrepareRenderPipeline",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)
            .Invoke(null,new object[]{pipeline});
        var camera=new GameObject("TerrainShadowCamera").AddComponent<Camera>();
        camera.transform.position=new Vector3(25,20,-30);camera.transform.LookAt(new Vector3(0,2,0));
        camera.backgroundColor=Color.gray;camera.clearFlags=CameraClearFlags.SolidColor;
        RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.15f,.15f,.15f);
        var spot=new GameObject("NightLamp").AddComponent<Light>();spot.type=LightType.Spot;
        spot.transform.position=new Vector3(-10,22,-10);spot.transform.LookAt(Vector3.zero);
        spot.range=100;spot.spotAngle=100;spot.intensity=1000;spot.shadows=LightShadows.Hard;spot.shadowBias=0;spot.shadowNormalBias=0;
        var receiver=GameObject.CreatePrimitive(PrimitiveType.Plane);receiver.transform.localScale=Vector3.one*8;
        receiver.GetComponent<Renderer>().sharedMaterial=new Material(Shader.Find("Universal Render Pipeline/Lit"));
        var rider=GameObject.CreatePrimitive(PrimitiveType.Cube);rider.name="MovingShadowCaster";
        rider.transform.position=new Vector3(15,1,-4);rider.transform.localScale=new Vector3(1,2,1);
        rider.GetComponent<Renderer>().sharedMaterial=receiver.GetComponent<Renderer>().sharedMaterial;
        var terrainData=new TerrainData { heightmapResolution=33,size=new Vector3(16,1,16) };
        terrainData.SetHeights(0,0,new float[33,33]);
        var terrain=Terrain.CreateTerrainGameObject(terrainData).GetComponent<Terrain>();terrain.transform.position=new Vector3(-8,5,-8);
        terrain.materialTemplate=new Material(Shader.Find("Universal Render Pipeline/Terrain/Lit"));
        terrainData.terrainLayers=new[]{new TerrainLayer { diffuseTexture=Texture2D.whiteTexture,tileSize=Vector2.one }};
        var target=new RenderTexture(320,240,24,RenderTextureFormat.ARGBHalf);target.Create();
        Color[] Render()
        {
            RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest { destination=target });
            return ReadPixels(target);
        }
        var baseline=Render();
        terrain.shadowCastingMode=ShadowCastingMode.Off;
        var unshadowed=Render();
        var proxy=new GameObject("ShadowOnlyTerrainProxy");proxy.transform.position=terrain.transform.position;
        var mesh=new Mesh();mesh.vertices=new[]{Vector3.zero,new Vector3(16,0,0),new Vector3(0,0,16),new Vector3(16,0,16)};
        mesh.normals=new[]{Vector3.up,Vector3.up,Vector3.up,Vector3.up};mesh.triangles=new[]{0,2,1,1,2,3};mesh.RecalculateBounds();
        proxy.AddComponent<MeshFilter>().sharedMesh=mesh;
        var proxyRenderer=proxy.AddComponent<MeshRenderer>();
        proxyRenderer.sharedMaterial=new Material(Shader.Find("Hidden/AlpineVision/TerrainShadow"));
        proxyRenderer.shadowCastingMode=ShadowCastingMode.ShadowsOnly;
        var replacement=Render();
        float nativeDifference=0,replacementError=0;
        for(var index=0;index<baseline.Length;index++)
        {
            nativeDifference+=Mathf.Abs(baseline[index].grayscale-unshadowed[index].grayscale);
            replacementError+=Mathf.Abs(baseline[index].grayscale-replacement[index].grayscale);
        }
        nativeDifference/=baseline.Length;replacementError/=baseline.Length;
        if(nativeDifference<.001f)throw new Exception("Shadow fixture did not produce measurable native spotlight shadows");
        if(replacementError>Mathf.Min(.01f,nativeDifference*.2f+.001f))throw new Exception("Terrain proxy shadow differs from native terrain: "+replacementError);
        rider.transform.position=new Vector3(18,1,-4);
        var movingReplacement=Render();
        proxy.SetActive(false);terrain.shadowCastingMode=ShadowCastingMode.On;
        var movingNative=Render();float movingError=0,movingDifference=0;
        for(var index=0;index<baseline.Length;index++)
        {
            movingError+=Mathf.Abs(movingReplacement[index].grayscale-movingNative[index].grayscale);
            movingDifference+=Mathf.Abs(movingReplacement[index].grayscale-replacement[index].grayscale);
        }
        if(movingError/baseline.Length>.01f || movingDifference/baseline.Length<.001f)
            throw new Exception("Moving caster shadows were lost or frozen by the terrain replacement");
        rider.transform.position=new Vector3(15,1,-4);
        proxy.SetActive(false);terrain.shadowCastingMode=ShadowCastingMode.On;
        var restored=Render();float restoreError=0;
        for(var index=0;index<baseline.Length;index++)restoreError=Mathf.Max(restoreError,Mathf.Abs(baseline[index].grayscale-restored[index].grayscale));
        if(restoreError>.001f)throw new Exception("Terrain shadow restoration changed the image");
        target.Release();Object.DestroyImmediate(target);
        GraphicsSettings.defaultRenderPipeline=null;QualitySettings.renderPipeline=null;
        Debug.Log($"PASS spotlight terrain shadows replaced by shadow-only mesh and restored; mean image error={replacementError}, original shadow contribution={nativeDifference}.");
    }
}
