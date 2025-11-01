Third-party DLLs required in this folder (LibDir)

Place the following assemblies here, matching the names exactly:

- 0Harmony.dll
- Ak.Wwise.Api.WAAPI.dll
- AK.Wwise.Unity.API.dll
- AK.Wwise.Unity.API.WwiseTypes.dll
- AK.Wwise.Unity.MonoBehaviour.dll
- AK.Wwise.Unity.Timeline.dll
- ALINE.dll
- andywiecko.BurstTriangulator.dll
- Animancer.dll
- Animancer.FSM.dll
- AstarPathfindingProject.dll
- BakeryRuntimeAssembly.dll
- Bilibili.BDS.dll
- Cinemachine.dll
- Clipper2Lib.dll
- com.rlabrecque.steamworks.net.dll
- DOTween.dll
- DOTween.Modules.dll
- DOTweenPro.dll
- Drawing.dll
- Eflatun.SceneReference.dll
- FMODUnity.dll
- ICSharpCode.SharpZipLib.dll
- ItemStatsSystem.dll
- LeTai.TranslucentImage.dll
- LeTai.TranslucentImage.Demo.dll
- LeTai.TranslucentImage.UniversalRP.dll
- LeTai.TrueShadow.dll
- LiteNetLib.dll
- Newtonsoft.Json.dll
- NodeCanvas.dll
- PackageTools.dll
- ParadoxNotion.dll
- Pathfinding.Ionic.Zip.Reduced.dll
- Plugins.dll
- ShapesSamples.dll
- Sirenix.OdinInspector.Attributes.dll
- Sirenix.Serialization.dll
- Sirenix.Serialization.Config.dll
- Sirenix.Utilities.dll
- SymmetryBreak.TastyGrassShader.dll
- SymmetryBreakStudio.TastyGrassShader.Examples.dll
- TeamSoda.Duckov.Core.dll
- TeamSoda.Duckov.Utilities.dll
- TeamSoda.MiniLocalizor.dll
- UISplineRenderer.dll
- UniTask.dll
- UniTask.Addressables.dll
- UniTask.DOTween.dll
- UniTask.TextMeshPro.dll
- Unity.Burst.dll
- Unity.InputSystem.dll
- Unity.InputSystem.ForUI.dll
- Unity.TextMeshPro.dll
- Unity.VisualScripting.Core.dll

Notes
- Some of these may already exist in your game installation or asset packages.
- LibDir can be changed in DuckovCoopMod.user.props if you keep DLLs elsewhere.
- Unity/Game Managed DLLs (UnityEngine.* modules, FMODUnity.dll, etc.) are typically placed under GameManaged, but this project currently resolves the above names from LibDir as configured in the csproj.

