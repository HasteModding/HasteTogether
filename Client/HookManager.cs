using System.Reflection;
using MonoMod.RuntimeDetour;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace HasteTogether;

public static class HookManager
{
		internal delegate void PlayerCharacter_UpdateDelegate(PlayerCharacter self);
		internal delegate void PersistentObjects_AwakeDelegate(PersistentObjects self);
		internal delegate bool RestartAppDelegate(AppId_t appId);
		internal delegate void PlayerAnimationHandler_JumpDelegate(PlayerAnimationHandler self);
		internal delegate void PlayerAnimationHandler_LandDelegate(PlayerAnimationHandler self, LandingType landingType, bool savedLanding);
    internal delegate void PlayerAnimationHandler_WallBounceDelegate(PlayerAnimationHandler self);
    internal delegate void PlayerAnimationHandler_WaveDelegate(PlayerAnimationHandler self);
    internal delegate void PlayerAnimationHandler_TakedamageAnimDelegate(PlayerAnimationHandler self, float damage, Transform sourceTransform, EffectSource source);
		internal delegate void PlayerAnimationHandler_SetShardAnimDelegate(PlayerAnimationHandler self, int animId);
    internal delegate void PlayerAnimationHandler_SetConfidenceDelegate(PlayerAnimationHandler self, float value);
    internal delegate void PlayerAnimationHandler_PlayAnimationDelegate(PlayerAnimationHandler self, string animationName);


		private static Hook? hookPlayerUpdate;
		private static Hook? hookPersistentAwake;
		private static Hook? hookRestartApp;
    private static Hook? hookAnimJump;
    private static Hook? hookAnimLand;
    private static Hook? hookAnimWallBounce;
    private static Hook? hookAnimWave;
    private static Hook? hookAnimTakeDamage;
    private static Hook? hookAnimSetShard;
    private static Hook? hookAnimSetConfidence;
    private static Hook? hookAnimPlayAnimation;

		internal static PlayerCharacter_UpdateDelegate? orig_PlayerCharacter_Update;
		internal static PersistentObjects_AwakeDelegate? orig_PersistentObjects_Awake;
		internal static RestartAppDelegate? orig_RestartApp;
		internal static PlayerAnimationHandler_JumpDelegate? orig_AnimJump;    
    internal static PlayerAnimationHandler_LandDelegate? orig_AnimLand;
    internal static PlayerAnimationHandler_WallBounceDelegate? orig_AnimWallBounce;
    internal static PlayerAnimationHandler_WaveDelegate? orig_AnimWave;
    internal static PlayerAnimationHandler_TakedamageAnimDelegate? orig_AnimTakeDamage;
    internal static PlayerAnimationHandler_SetShardAnimDelegate? orig_AnimSetShard;
    internal static PlayerAnimationHandler_SetConfidenceDelegate? orig_AnimSetConfidence;
    internal static PlayerAnimationHandler_PlayAnimationDelegate? orig_AnimPlayAnimation;

		private static bool lastGrappleState = false;
    private static Vector3 lastGrappleVector = Vector3.zero;

    public static void InstallHooks()
    {
        Debug.Log("[HasteTogetherP2P] Installing hooks...");
        // --- Install existing hooks ---
        InstallHook("PlayerCharacter.Update", typeof(PlayerCharacter), "Update", ref hookPlayerUpdate, ref orig_PlayerCharacter_Update, PlayerCharacter_UpdateDetour);
        InstallHook("PersistentObjects.Awake", typeof(PersistentObjects), "Awake", ref hookPersistentAwake, ref orig_PersistentObjects_Awake, PersistentObjects_AwakeDetour);
        InstallHook("SteamAPI.RestartAppIfNecessary", typeof(SteamAPI), "RestartAppIfNecessary", ref hookRestartApp, ref orig_RestartApp, RestartAppDetour, BindingFlags.Static | BindingFlags.Public);

        // --- Install PlayerAnimationHandler Hooks ---
        InstallHook("PlayerAnimationHandler.Jump", typeof(PlayerAnimationHandler), "Jump", ref hookAnimJump, ref orig_AnimJump, PlayerAnimationHandler_JumpDetour);
        InstallHook("PlayerAnimationHandler.Land", typeof(PlayerAnimationHandler), "Land", ref hookAnimLand, ref orig_AnimLand, PlayerAnimationHandler_LandDetour);
        InstallHook("PlayerAnimationHandler.WallBounce", typeof(PlayerAnimationHandler), "WallBounce", ref hookAnimWallBounce, ref orig_AnimWallBounce, PlayerAnimationHandler_WallBounceDetour);
        InstallHook("PlayerAnimationHandler.Wave", typeof(PlayerAnimationHandler), "Wave", ref hookAnimWave, ref orig_AnimWave, PlayerAnimationHandler_WaveDetour);
        // TakeDamageAnim is private, need NonPublic flag
        InstallHook("PlayerAnimationHandler.TakedamageAnim", typeof(PlayerAnimationHandler), "TakedamageAnim", ref hookAnimTakeDamage, ref orig_AnimTakeDamage, PlayerAnimationHandler_TakedamageAnimDetour, BindingFlags.Instance | BindingFlags.NonPublic);
        InstallHook("PlayerAnimationHandler.SetShardAnim", typeof(PlayerAnimationHandler), "SetShardAnim", ref hookAnimSetShard, ref orig_AnimSetShard, PlayerAnimationHandler_SetShardAnimDetour);
        InstallHook("PlayerAnimationHandler.SetConfidence", typeof(PlayerAnimationHandler), "SetConfidence", ref hookAnimSetConfidence, ref orig_AnimSetConfidence, PlayerAnimationHandler_SetConfidenceDetour);
        InstallHook("PlayerAnimationHandler.PlayAnimation", typeof(PlayerAnimationHandler), "PlayAnimation", ref hookAnimPlayAnimation, ref orig_AnimPlayAnimation, PlayerAnimationHandler_PlayAnimationDetour);
    }

    private static void PlayerCharacter_UpdateDetour(PlayerCharacter self)
    {
        orig_PlayerCharacter_Update?.Invoke(self); // Call original first

        // --- Send Position Updates ---
        try
        {
            if (Plugin.networkManager != null && Plugin.networkManager.IsInLobby && IsLocalPlayerCharacter(self)) // Check if it's the local player
            {
                // Include grounded state in the update packet
                UpdatePacket packet = new UpdatePacket(self.transform, self.data.mostlyGrounded);
                if (packet.HasChanged(Plugin.lastSent, 0.0001f, 0.1f))
                {
                    packet.Broadcast(SendType.Unreliable);
                    Plugin.lastSent = packet;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[HasteTogetherP2P] Error sending position update: {ex}");
        }

        // --- Send Grapple State Updates ---
        try
        {
             if (Plugin.networkManager != null && Plugin.networkManager.IsInLobby && IsLocalPlayerCharacter(self))
             {
                 bool currentGrappleState = self.data.sinceGrapple < 0.1f;
                 Vector3 currentGrappleVector = self.data.grappleVector; // Assuming this is updated correctly by game

                 // Send only if state or vector changed significantly
                 if (currentGrappleState != lastGrappleState || (currentGrappleState && (currentGrappleVector - lastGrappleVector).sqrMagnitude > 0.01f))
                 {
                     new GrappleStatePacket { IsGrappling = currentGrappleState, GrappleVector = currentGrappleVector }
                         .Broadcast(SendType.Reliable);
                     lastGrappleState = currentGrappleState;
                     lastGrappleVector = currentGrappleVector;
                 }
             }
        }
        catch (Exception ex)
        {
             Debug.LogError($"[HasteTogetherP2P] Error sending grapple state: {ex}");
        }
    }

     private static void PersistentObjects_AwakeDetour(PersistentObjects self)
    {
        Debug.Log("[HasteTogetherP2P] PersistentObjects_AwakeDetour called.");
        orig_PersistentObjects_Awake?.Invoke(self); // Call original first
        Debug.Log("[HasteTogetherP2P] Original PersistentObjects.Awake finished.");

        // --- UI Setup Logic ---
        if (self == null || self.gameObject == null)
        {
            Debug.LogWarning("[HasteTogetherP2P] PersistentObjects instance is null after Awake. Skipping UI setup.");
            return;
        }
        Transform? persistentUIRoot = self.transform.Find("UI_Persistent");
        if (persistentUIRoot == null)
        {
             Debug.LogWarning("[HasteTogetherP2P] 'UI_Persistent' not found under PersistentObjects. Creating...");
             // Create basic UI root if missing (adapt as needed for the game's UI structure)
             GameObject uiRootObj = new GameObject("UI_Persistent");
             persistentUIRoot = uiRootObj.transform;
             persistentUIRoot.SetParent(self.transform, false);
             Canvas rootCanvas = uiRootObj.GetComponent<Canvas>() ?? uiRootObj.AddComponent<Canvas>();
             rootCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
             if (uiRootObj.GetComponent<CanvasScaler>() == null) uiRootObj.AddComponent<CanvasScaler>();
             if (uiRootObj.GetComponent<GraphicRaycaster>() == null) uiRootObj.AddComponent<GraphicRaycaster>();
        }

        if (Plugin.TogetherUI == null && persistentUIRoot != null)
        {
            try
            {
                Plugin.TogetherUI = new GameObject("TogetherP2P_UI").transform;
                Plugin.TogetherUI.SetParent(persistentUIRoot, false);
                Plugin.TogetherUI.localPosition = Vector3.zero;
                Plugin.TogetherUI.localScale = Vector3.one;
                Debug.Log("[HasteTogetherP2P] Created TogetherP2P_UI root under UI_Persistent.");

                // Create Lobby Status Image
                GameObject statusImgObj = new GameObject("LobbyStatusImage");
                statusImgObj.transform.SetParent(Plugin.TogetherUI, false);
                Image statusImage = statusImgObj.AddComponent<Image>();
                RectTransform imgRect = statusImgObj.GetComponent<RectTransform>();
                imgRect.anchorMin = new Vector2(1, 1); imgRect.anchorMax = new Vector2(1, 1);
                imgRect.pivot = new Vector2(1, 1);
                imgRect.anchoredPosition = new Vector2(-20, -20); // Top-right corner offset
                imgRect.sizeDelta = new Vector2(64, 64);
                statusImage.preserveAspect = true;

                LobbyStatusUI statusUI = statusImgObj.AddComponent<LobbyStatusUI>();
                statusUI.img = statusImage;
                // Load sprites (ensure resource names are correct and embedded)
                statusUI.inLobbySprite = LoadSpriteFromResource("HasteTogether.P2P.Graphics.HasteTogether_Connected.png")!;
                statusUI.notInLobbySprite = LoadSpriteFromResource("HasteTogether.P2P.Graphics.HasteTogether_Disconnected.png")!;
                if (statusUI.inLobbySprite == null || statusUI.notInLobbySprite == null) Debug.LogError("[HasteTogetherP2P] Failed to load status sprites!");

                // Create Lobby ID Text
                GameObject lobbyIdObj = new GameObject("LobbyIdText");
                lobbyIdObj.transform.SetParent(Plugin.TogetherUI, false);
                TextMeshProUGUI lobbyText = lobbyIdObj.AddComponent<TextMeshProUGUI>();
                RectTransform textRect = lobbyIdObj.GetComponent<RectTransform>();
                textRect.anchorMin = new Vector2(1, 1); textRect.anchorMax = new Vector2(1, 1);
                textRect.pivot = new Vector2(1, 1);
                textRect.anchoredPosition = new Vector2(-100, -25); // Near image
                textRect.sizeDelta = new Vector2(300, 30);
                lobbyText.fontSize = 14;
                lobbyText.color = Color.white;
                lobbyText.alignment = TextAlignmentOptions.TopRight;
                lobbyText.text = "Initializing...";
                if (TMP_Settings.defaultFontAsset != null) lobbyText.font = TMP_Settings.defaultFontAsset;
                statusUI.lobbyIdText = lobbyText;

                Debug.Log("[HasteTogetherP2P] Persistent UI setup attempt complete.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HasteTogetherP2P] Error setting up TogetherP2P_UI: {ex}");
                if (Plugin.TogetherUI != null) GameObject.Destroy(Plugin.TogetherUI.gameObject);
                Plugin.TogetherUI = null!;
            }
        }
        else if (Plugin.TogetherUI != null) Debug.LogWarning("[HasteTogetherP2P] TogetherP2P_UI already exists.");
        else Debug.LogError("[HasteTogetherP2P] Cannot setup UI - Persistent UI Root not found/created.");
    }

		private static bool RestartAppDetour(AppId_t appId)
    {
        Debug.LogWarning($"[HasteTogetherP2P] SteamAPI.RestartAppIfNecessary called for AppId: {appId}. Preventing restart.");
        return false; // Prevent the restart
    }


		private static void PlayerAnimationHandler_JumpDetour(PlayerAnimationHandler self)
    {
        orig_AnimJump?.Invoke(self); // Call original first
        if (IsLocalPlayerAnimationHandler(self))
        {
            new JumpPacket().Broadcast(SendType.Reliable);
        }
    }

		
    private static void PlayerAnimationHandler_LandDetour(PlayerAnimationHandler self, LandingType landingType, bool savedLanding)
    {
        orig_AnimLand?.Invoke(self, landingType, savedLanding); // Call original first
        if (IsLocalPlayerAnimationHandler(self))
        {
            new LandPacket { LandingType = landingType, SavedLanding = savedLanding }.Broadcast(SendType.Reliable);
        }
    }

		private static void PlayerAnimationHandler_WallBounceDetour(PlayerAnimationHandler self)
    {
        orig_AnimWallBounce?.Invoke(self);
        if (IsLocalPlayerAnimationHandler(self))
        {
            new WallBouncePacket().Broadcast(SendType.Reliable);
        }
    }
    
		private static void PlayerAnimationHandler_WaveDetour(PlayerAnimationHandler self)
    {
        orig_AnimWave?.Invoke(self);
        if (IsLocalPlayerAnimationHandler(self))
        {
            new WavePacket().Broadcast(SendType.Reliable);
        }
    }

		// Special handling for TakeDamage to capture calculated direction
    private static void PlayerAnimationHandler_TakedamageAnimDetour(PlayerAnimationHandler self, float damage, Transform sourceTransform, EffectSource source)
    {
        // Call original FIRST to let it calculate dmgDirValue
        orig_AnimTakeDamage?.Invoke(self, damage, sourceTransform, source);

        if (IsLocalPlayerAnimationHandler(self))
        {
            // Access the calculated value (needs reflection or making the field public temporarily during patching)
            FieldInfo? dmgDirField = typeof(PlayerAnimationHandler).GetField("dmgDirValue", BindingFlags.NonPublic | BindingFlags.Instance);
            float dirValue = (dmgDirField != null) ? (float)dmgDirField.GetValue(self) : 0f;

            new TakeDamagePacket { DamageDirectionValue = dirValue }.Broadcast(SendType.Reliable);
        }
    }
		
		private static void PlayerAnimationHandler_SetShardAnimDetour(PlayerAnimationHandler self, int animId)
    {
        orig_AnimSetShard?.Invoke(self, animId);
        if (IsLocalPlayerAnimationHandler(self))
        {
            new SetShardAnimPacket{ AnimationId = animId }.Broadcast(SendType.Reliable);
        }
    }

    private static void PlayerAnimationHandler_SetConfidenceDetour(PlayerAnimationHandler self, float value)
    {
        orig_AnimSetConfidence?.Invoke(self, value);
        if (IsLocalPlayerAnimationHandler(self))
        {
            new SetConfidencePacket{ ConfidenceValue = value }.Broadcast(SendType.Reliable);
        }
    }

    private static void PlayerAnimationHandler_PlayAnimationDetour(PlayerAnimationHandler self, string animationName)
    {
        orig_AnimPlayAnimation?.Invoke(self, animationName);
        if (IsLocalPlayerAnimationHandler(self))
        {
            new PlayAnimationPacket{ AnimationName = animationName }.Broadcast(SendType.Reliable);
        }
    }

		private static void InstallHook<TDelegate>(string hookName, Type targetType, string methodName, ref Hook? hookField, ref TDelegate? origField, TDelegate detourDelegate, BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type[]? parameterTypes = null) where TDelegate : Delegate
    {
         MethodInfo? methodInfo = parameterTypes == null
            ? targetType.GetMethod(methodName, flags)
            : targetType.GetMethod(methodName, flags, null, parameterTypes, null);

        if (methodInfo != null)
        {
            try
            {
                hookField = new Hook(methodInfo, detourDelegate);
                origField = hookField.GenerateTrampoline<TDelegate>();
                Debug.Log($"[HasteTogetherP2P] Hooked {hookName} successfully.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HasteTogetherP2P] Failed to hook {hookName}: {ex}");
            }
        }
        else
        {
            Debug.LogError($"[HasteTogetherP2P] Could not find method for hook: {hookName} in {targetType.FullName}");
        }
    }

		/// --- Helpers --- ///

		private static bool IsLocalPlayerAnimationHandler(PlayerAnimationHandler handler)
    {
        // Assumes PlayerCharacter.localPlayer is valid and handler is attached to it
        return handler != null && PlayerCharacter.localPlayer != null && handler.gameObject == PlayerCharacter.localPlayer.gameObject;
    }

		// Helper to check if PlayerCharacter is local
    private static bool IsLocalPlayerCharacter(PlayerCharacter pc)
    {
        return pc != null && PlayerCharacter.localPlayer != null && pc == PlayerCharacter.localPlayer;
    }

		// Helper to load Sprites from Embedded Resources
    private static Sprite? LoadSpriteFromResource(string resourceName)
    {
        try
        {
            Assembly executingAssembly = Assembly.GetExecutingAssembly();
            using Stream? imgStream = executingAssembly.GetManifestResourceStream(resourceName);
            if (imgStream == null)
            {
                string available = string.Join(", ", executingAssembly.GetManifestResourceNames());
                Debug.LogError($"[HasteTogetherP2P] Embedded resource not found: {resourceName}. Available: {available}");
                return null;
            }
            using MemoryStream ms = new MemoryStream();
            imgStream.CopyTo(ms);
            byte[] buffer = ms.ToArray();
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point, // Good for pixel art UI
                wrapMode = TextureWrapMode.Clamp
            };
            if (texture.LoadImage(buffer))
            {
                Sprite sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100.0f);
                sprite.name = resourceName;
                return sprite;
            }
            else
            {
                Debug.LogError($"[HasteTogetherP2P] Failed to load image data from resource: {resourceName}");
                GameObject.Destroy(texture);
                return null;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[HasteTogetherP2P] Exception loading sprite from resource '{resourceName}': {ex}");
            return null;
        }
    }
}
