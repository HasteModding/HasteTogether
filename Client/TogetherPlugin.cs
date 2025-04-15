using System.Reflection;
using System.Text;
using Landfall.Haste;
using Landfall.Modding;
using MonoMod.RuntimeDetour;
using SettingsLib.Settings;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;
using Zorro.Core.CLI;
using Zorro.Settings;

namespace HasteTogether;

[LandfallPlugin]
public class Plugin
{
    public static NetworkManager networkManager = null!;
    public static UpdatePacket lastSent = new UpdatePacket();
    public static Transform TogetherUI = null!;
    public static CSteamID LocalSteamId { get; private set; } // Use CSteamID
    public static Dictionary<CSteamID, NetworkedPlayer> networkedPlayers =
        new Dictionary<CSteamID, NetworkedPlayer>(); // Use CSteamID

    // Field to hold public lobby name during creation process
    public static string? PublicLobbyName = null;

    // Static reference to the public lobby list setting instance for UI updates
    public static PublicLobbyListSetting? PublicLobbyListSettingInstance;

    // Static cache of current public lobbies (optional, data is mainly in the setting now)
    public static List<PublicLobbyInfo> CurrentPublicLobbies = new List<PublicLobbyInfo>();

    // Static cache for last sent animation packet to avoid redundant sends
    public static AnimationPacket? lastAnimationPacket;

    // Static constructor
    static Plugin()
    {
        Debug.Log("[HasteTogether] Plugin loading...");
        // Create the network manager and install hooks
        networkManager = new NetworkManager();
        Debug.Log("[HasteTogether] NetworkManager created.");
        HookManager.InstallHooks(); // Install MonoMod hooks

        // GameObject for Steam Initialization
        GameObject steamInitGO = new GameObject("SteamInitializer");
        GameObject.DontDestroyOnLoad(steamInitGO);
        steamInitGO.AddComponent<SteamInitializer>();

        // GameObject to run Steam Callbacks
        GameObject steamCallbackRunner = new GameObject("SteamCallbackRunner");
        GameObject.DontDestroyOnLoad(steamCallbackRunner);
        steamCallbackRunner.AddComponent<SteamCallbacks>();
        Debug.Log("[HasteTogether] Plugin loaded successfully!");
    }

    // Sets the local Steam ID (called once Steam is initialized)
    public static void SetLocalSteamId(CSteamID id) // Use CSteamID
    {
        LocalSteamId = id;
        Debug.Log($"[HasteTogether] Local Steam ID set to: {LocalSteamId}");
    }

    // Creates or retrieves a NetworkedPlayer instance for a given Steam ID
    public static NetworkedPlayer? GetOrSetupPlayer(CSteamID steamId) // Use CSteamID
    {
        if (steamId == LocalSteamId || !steamId.IsValid())
            return null;
        if (networkedPlayers.TryGetValue(steamId, out NetworkedPlayer? existingPlayer))
            return existingPlayer;
        return SetupNetworkedPlayer(steamId);
    }

    // Creates a new NetworkedPlayer instance
    public static NetworkedPlayer? SetupNetworkedPlayer(CSteamID steamId) // Use CSteamID
    {
        if (steamId == LocalSteamId || !steamId.IsValid())
            return null;
        if (networkedPlayers.ContainsKey(steamId))
            return networkedPlayers[steamId];

        // Find a local player model to clone
        // TODO: Replace FindObjectOfType if it causes performance issues. Cache or use a better method.
        PlayerModel model = GameObject.FindObjectOfType<PlayerModel>();
        if (model != null)
        {
            GameObject newPlayer = GameObject.Instantiate(model.gameObject);
            // Disable components controlling local player logic
            var pmc = newPlayer.GetComponent<PlayerModel>();
            if (pmc != null)
                pmc.enabled = false;
            var pcc = newPlayer.GetComponent<PlayerCharacter>();
            if (pcc != null)
                pcc.enabled = false;
            // TODO: Disable other input/camera components if necessary

            newPlayer.transform.position = model.gameObject.transform.position;
            newPlayer.name = $"HasteTogether_{steamId}"; // Name updated later
            NetworkedPlayer np = newPlayer.AddComponent<NetworkedPlayer>();
            np.steamId = steamId;
            np.animator = newPlayer.GetComponentInChildren<Animator>(); // Ensure this finds the correct animator
            if (np.animator == null)
                Debug.LogWarning($"[HasteTogether] Animator not found for player {steamId}");

            networkedPlayers.Add(steamId, np);
            Debug.Log(
                $"[HasteTogether] Created NetworkedPlayer for {steamId}. Waiting for name."
            );
            // Request name immediately? Or wait for NamePacket?
            // Example: Send own name back if needed (handled in OnLobbyEntered now)
            return np;
        }
        Debug.LogError("[HasteTogether] [ERROR] PlayerModel not found in scene to clone.");
        return null;
    }

    // Removes a NetworkedPlayer instance and destroys its GameObject
    public static void RemoveNetworkedPlayer(CSteamID steamId) // Use CSteamID
    {
        if (networkedPlayers.TryGetValue(steamId, out NetworkedPlayer? player))
        {
            if (player != null && player.gameObject != null)
                GameObject.Destroy(player.gameObject);
            networkedPlayers.Remove(steamId);
            Debug.Log($"[HasteTogether] Removed NetworkedPlayer for {steamId}.");
        }
    }

    // Serializes transform data into a compact byte array
    public static byte[] SerializeTransform(Vector3 position, Quaternion rotation)
    {
        byte[] buffer = new byte[15];
        // Clamp values to prevent overflow/underflow during conversion
        position.x = Mathf.Clamp(position.x, -32767.5f, 32767.0f);
        position.y = Mathf.Clamp(position.y, -32767.5f, 32767.0f);
        position.z = Mathf.Clamp(position.z, -32767.5f, 32767.0f);
        // Convert position floats to 24-bit integers (scaled)
        int x = (int)((position.x + 32767.5f) * 256.0f);
        int y = (int)((position.y + 32767.5f) * 256.0f);
        int z = (int)((position.z + 32767.5f) * 256.0f);
        buffer[0] = (byte)(x >> 16);
        buffer[1] = (byte)(x >> 8);
        buffer[2] = (byte)x;
        buffer[3] = (byte)(y >> 16);
        buffer[4] = (byte)(y >> 8);
        buffer[5] = (byte)y;
        buffer[6] = (byte)(z >> 16);
        buffer[7] = (byte)(z >> 8);
        buffer[8] = (byte)z;
        // Normalize rotation and clamp components
        rotation.Normalize();
        float clampedY = Mathf.Clamp(rotation.y, -1.0f, 1.0f);
        float clampedW = Mathf.Clamp(rotation.w, -1.0f, 1.0f);
        // Convert rotation floats (y, w) to 24-bit integers (scaled)
        int rotY = Mathf.RoundToInt((clampedY + 1.0f) * 8388607.5f); // Scale [-1, 1] to [0, 16777215]
        int rotW = Mathf.RoundToInt((clampedW + 1.0f) * 8388607.5f);
        buffer[9] = (byte)((rotY >> 16) & 0xFF);
        buffer[10] = (byte)((rotY >> 8) & 0xFF);
        buffer[11] = (byte)(rotY & 0xFF);
        buffer[12] = (byte)((rotW >> 16) & 0xFF);
        buffer[13] = (byte)((rotW >> 8) & 0xFF);
        buffer[14] = (byte)(rotW & 0xFF);
        return buffer;
    }

    /// <summary>
    /// Checks if the animation state has changed and sends the packet if needed.
    /// </summary>
    public static void SendAnimationPacketIfNeeded(AnimationPacket packet)
    {
        // Compare with the last sent packet (handle null case)
        if (lastAnimationPacket != null && lastAnimationPacket.Equals(packet))
        {
            return; // No change, don't send
        }
        // Send reliably via broadcast (adjust if needed)
        packet.Broadcast(SendType.Reliable);
        lastAnimationPacket = packet; // Update the cache
    }

    /// <summary>
    /// Called by NetworkManager when the public lobby list is received from Steam.
    /// Updates the static cache and the UI setting instance.
    /// </summary>
    public static void UpdatePublicLobbyList(List<PublicLobbyInfo> lobbyInfos)
    {
        Debug.Log("[HasteTogether] Updating public lobby list:");
        foreach (var lobby in lobbyInfos)
        {
            Debug.Log($"  Lobby: {lobby.LobbyName} (ID: {lobby.LobbyID})");
        }
        CurrentPublicLobbies = lobbyInfos; // Update static cache (optional)

        // Update the UI setting instance if it exists
        if (PublicLobbyListSettingInstance != null)
        {
            PublicLobbyListSettingInstance.UpdateEntries(lobbyInfos);
        }
        else
        {
            Debug.LogWarning(
                "[HasteTogether] PublicLobbyListSettingInstance is null. UI will not update."
            );
        }
    }

    // --- Console Commands ---
    [ConsoleCommand]
    public static void CreatePrivateLobbyCommand()
    {
        if (networkManager != null)
        {
            PublicLobbyName = null; // Ensure no public name is set
            networkManager.CreateLobby(ELobbyType.k_ELobbyTypePrivate);
        }
        else
        {
            Debug.LogError("[HasteTogether] NetworkManager not initialized.");
        }
    }

    [ConsoleCommand]
    public static void CreatePublicLobbyCommand(string lobbyName)
    {
        if (networkManager != null)
        {
            if (string.IsNullOrWhiteSpace(lobbyName))
            {
                Debug.LogError("[HasteTogether] Public lobby name cannot be empty.");
                return;
            }
            PublicLobbyName = lobbyName; // Store name for OnLobbyCreated callback
            networkManager.CreateLobby(ELobbyType.k_ELobbyTypePublic);
        }
        else
        {
            Debug.LogError("[HasteTogether] NetworkManager not initialized.");
        }
    }

    [ConsoleCommand]
    public static void JoinLobbyCommand(string lobbyCode) // Renamed for clarity, handles both private/public IDs
    {
        if (networkManager == null)
        {
            Debug.LogError("[HasteTogether] NetworkManager not initialized.");
            return;
        }

        if (ulong.TryParse(lobbyCode, out ulong lobbyIdNumeric))
        {
            CSteamID lobbyId = new CSteamID(lobbyIdNumeric); // Construct CSteamID from ulong
            if (lobbyId.IsValid())
            {
                networkManager.JoinLobby(lobbyId);
            }
            else
            {
                Debug.LogError($"[HasteTogether] Invalid lobby ID format: {lobbyCode}");
            }
        }
        else
        {
            Debug.LogError($"[HasteTogether] Invalid lobby code/ID provided: {lobbyCode}");
        }
    }
}

// --- Steam Initializer ---
public class SteamInitializer : MonoBehaviour
{
    [SerializeField]
    private AppId_t appId = new AppId_t(1796470); // Haste's App ID

    private bool isSteamInitialized = false;

    void Awake()
    {
        // Prevent duplicate initialization
        if (FindObjectsOfType<SteamInitializer>().Length > 1)
        {
            Debug.LogWarning(
                "[SteamInitializer] Another instance already exists. Destroying this one."
            );
            Destroy(gameObject);
            return;
        }

        try
        {
            // Standard Steamworks.NET Initialization Checks (Optional but helpful)
            if (!Packsize.Test())
                Debug.LogError("[SteamInitializer] Packsize Test failed. Structure size mismatch.");
            if (!DllCheck.Test())
                Debug.LogError(
                    "[SteamInitializer] DllCheck Test failed. Core Steamworks binaries missing/corrupt."
                );

            // Restart if not launched via Steam
            if (SteamAPI.RestartAppIfNecessary(appId))
            {
                Debug.LogWarning("[SteamInitializer] Restarting via Steam...");
                Application.Quit();
                return;
            }

            // Initialize Steam API
            if (!SteamAPI.Init())
            {
                Debug.LogError(
                    "[SteamInitializer] SteamAPI.Init() failed. Is Steam running? Is steam_appid.txt present?"
                );
                enabled = false; // Disable component if Steam fails
                return;
            }

            isSteamInitialized = true;
            CSteamID localId = SteamUser.GetSteamID();
            Debug.Log($"[SteamInitializer] SteamAPI initialized successfully. Local ID: {localId}");

            // Pass the Steam ID to the Plugin
            Plugin.SetLocalSteamId(localId);
        }
        catch (DllNotFoundException e)
        {
            Debug.LogError(
                $"[SteamInitializer] Steamworks native library not found: {e.Message}. Ensure SDK redistributables are present."
            );
            enabled = false;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SteamInitializer] Unexpected error initializing Steam: {ex}");
            enabled = false;
        }
    }

    void OnDestroy()
    {
        ShutdownSteam();
    }

    void OnApplicationQuit()
    {
        ShutdownSteam();
    }

    private void ShutdownSteam()
    {
        if (isSteamInitialized)
        {
            SteamAPI.Shutdown();
            Debug.Log("[SteamInitializer] SteamAPI Shutdown.");
            isSteamInitialized = false;
        }
    }
}

// --- Steam Callbacks Runner ---
public class SteamCallbacks : MonoBehaviour
{
    void Update()
    {
        try
        {
            // Run Steamworks callbacks (essential for networking events)
            SteamAPI.RunCallbacks();

            // Receive P2P packets if the network manager exists
            Plugin.networkManager?.ReceiveP2PPackets();
        }
        catch (Exception e)
        {
            Debug.LogError($"[HasteTogether] Error in SteamCallbacks Update: {e}");
            // Consider disabling only if the error is critical/persistent
            // this.enabled = false;
        }
    }

    // Ensure lobby is left if this component is destroyed unexpectedly or on quit
    void OnDestroy()
    {
        Plugin.networkManager?.LeaveLobby();
    }

    void OnApplicationQuit()
    {
        Plugin.networkManager?.LeaveLobby();
    }
}

// --- Network Manager (Handles Steam Lobby and P2P) ---
public partial class NetworkManager // Made partial for potential future extensions
{
    public CSteamID CurrentLobby { get; private set; } = CSteamID.Nil;
    public HashSet<CSteamID> Peers { get; private set; } = new HashSet<CSteamID>();
    public bool IsInLobby => CurrentLobby.IsValid() && CurrentLobby != CSteamID.Nil;
    public bool IsLobbyHost =>
        IsInLobby && SteamMatchmaking.GetLobbyOwner(CurrentLobby) == Plugin.LocalSteamId;

    // Steamworks Callbacks and CallResults
    protected Callback<LobbyCreated_t> m_LobbyCreatedCallback;
    protected Callback<LobbyEnter_t> m_LobbyEnteredCallback;
    protected Callback<LobbyChatUpdate_t> m_LobbyChatUpdateCallback;
    protected Callback<LobbyDataUpdate_t> m_LobbyDataUpdateCallback;
    protected Callback<P2PSessionRequest_t> m_P2PSessionRequestCallback;
    protected Callback<P2PSessionConnectFail_t> m_P2PSessionConnectFailCallback;

    private CallResult<LobbyCreated_t> m_CreateLobbyCallResult;
    private CallResult<LobbyEnter_t> m_JoinLobbyCallResult;
    public CallResult<LobbyMatchList_t> m_LobbyMatchListCallResult; // For public lobby list

    public NetworkManager()
    {
        // Initialize Callbacks
        m_LobbyCreatedCallback = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
        m_LobbyEnteredCallback = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
        m_LobbyChatUpdateCallback = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
        m_LobbyDataUpdateCallback = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataChanged);
        m_P2PSessionRequestCallback = Callback<P2PSessionRequest_t>.Create(OnP2PSessionRequest);
        m_P2PSessionConnectFailCallback = Callback<P2PSessionConnectFail_t>.Create(
            OnP2PSessionConnectFail
        );

        // Initialize CallResults
        m_CreateLobbyCallResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreatedResult);
        m_JoinLobbyCallResult = CallResult<LobbyEnter_t>.Create(OnLobbyEnteredResult);
        m_LobbyMatchListCallResult = CallResult<LobbyMatchList_t>.Create(OnLobbyMatchList); // Init public list result

        Debug.Log("[HasteTogether] NetworkManager initialized with Steamworks.NET callbacks.");
    }

    // --- Lobby Actions ---
    public void CreateLobby(ELobbyType lobbyType, int maxMembers = 8)
    {
        if (IsInLobby)
        {
            Debug.LogWarning("[HasteTogether] Already in a lobby.");
            return;
        }
        Debug.Log($"[HasteTogether] Requesting Steam Lobby creation (Type: {lobbyType})...");
        SteamAPICall_t hSteamAPICall = SteamMatchmaking.CreateLobby(lobbyType, maxMembers);
        m_CreateLobbyCallResult.Set(hSteamAPICall);
    }

    public void JoinLobby(CSteamID lobbyId)
    {
        if (IsInLobby)
        {
            Debug.LogWarning("[HasteTogether] Already in a lobby.");
            // Optionally leave current lobby first?
            // LeaveLobby();
            return;
        }
        if (!lobbyId.IsValid())
        {
            Debug.LogError("[HasteTogether] Invalid Lobby ID provided for joining.");
            return;
        }
        Debug.Log($"[HasteTogether] Attempting to join lobby {lobbyId}...");
        SteamAPICall_t hSteamAPICall = SteamMatchmaking.JoinLobby(lobbyId);
        m_JoinLobbyCallResult.Set(hSteamAPICall);
    }

    public void LeaveLobby()
    {
        if (IsInLobby)
        {
            CSteamID lobbyToLeave = CurrentLobby;
            Debug.Log($"[HasteTogether] Leaving lobby {lobbyToLeave}...");
            SteamMatchmaking.LeaveLobby(lobbyToLeave);
            CleanupLobbyState(); // Clean up immediately
        }
    }

    private void CleanupLobbyState()
    {
        Debug.Log("[HasteTogether] Cleaning up lobby state.");
        CurrentLobby = CSteamID.Nil;

        // Close P2P sessions
        foreach (CSteamID peerId in Peers)
        {
            if (peerId != Plugin.LocalSteamId)
            {
                Debug.Log($"[HasteTogether] Closing P2P session with {peerId}");
                SteamNetworking.CloseP2PSessionWithUser(peerId);
            }
        }
        Peers.Clear();

        // Destroy networked player objects
        // Create a temporary list to avoid modification during iteration issues
        List<CSteamID> playersToRemove = Plugin.networkedPlayers.Keys.ToList();
        foreach (CSteamID steamId in playersToRemove)
        {
            Plugin.RemoveNetworkedPlayer(steamId);
        }
        Plugin.networkedPlayers.Clear(); // Ensure dictionary is cleared
    }

    // --- Steam Callback Handlers ---

    // CallResult handler for CreateLobby
    private void OnLobbyCreatedResult(LobbyCreated_t pCallback, bool bIOFailure)
    {
        Debug.Log(
            $"[HasteTogether] OnLobbyCreatedResult: Result={pCallback.m_eResult}, IOFailure={bIOFailure}, LobbyID={pCallback.m_ulSteamIDLobby}"
        );
        if (bIOFailure || pCallback.m_eResult != EResult.k_EResultOK)
        {
            Debug.LogError(
                $"[HasteTogether] Lobby creation failed! Result: {pCallback.m_eResult}, IOFailure: {bIOFailure}"
            );
            CurrentLobby = CSteamID.Nil;
            Plugin.PublicLobbyName = null; // Clear pending name on failure
            return;
        }

        CurrentLobby = new CSteamID(pCallback.m_ulSteamIDLobby);
        Debug.Log(
            $"[HasteTogether] Lobby {CurrentLobby} created successfully! You are the host."
        );

        // Set initial lobby data
        SteamMatchmaking.SetLobbyData(CurrentLobby, "HasteTogetherVersion", "P2P_SWNET_1.0"); // Example version
        SteamMatchmaking.SetLobbyData(CurrentLobby, "HostName", SteamFriends.GetPersonaName());

        // Set public lobby name if provided
        if (!string.IsNullOrEmpty(Plugin.PublicLobbyName))
        {
            SteamMatchmaking.SetLobbyData(CurrentLobby, "LobbyName", Plugin.PublicLobbyName);
            Debug.Log($"[HasteTogether] Public lobby name set to: {Plugin.PublicLobbyName}");
            Plugin.PublicLobbyName = null; // Clear the name after setting it
        }

        // Add self to peers list
        Peers.Add(Plugin.LocalSteamId);
        // OnLobbyEntered will likely fire next for us.
    }

    // CallResult handler for JoinLobby
    private void OnLobbyEnteredResult(LobbyEnter_t pCallback, bool bIOFailure)
    {
        Debug.Log(
            $"[HasteTogether] OnLobbyEnteredResult: Response={(EChatRoomEnterResponse)pCallback.m_EChatRoomEnterResponse}, IOFailure={bIOFailure}, LobbyID={pCallback.m_ulSteamIDLobby}"
        );

        if (bIOFailure)
        {
            Debug.LogError(
                $"[HasteTogether] Failed to join lobby {pCallback.m_ulSteamIDLobby} due to IO Failure."
            );
            CurrentLobby = CSteamID.Nil;
            return;
        }

        CSteamID lobbyId = new CSteamID(pCallback.m_ulSteamIDLobby);
        if (
            pCallback.m_EChatRoomEnterResponse
            != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess
        )
        {
            Debug.LogError(
                $"[HasteTogether] Failed to enter lobby {lobbyId}. Response: {(EChatRoomEnterResponse)pCallback.m_EChatRoomEnterResponse}"
            );
            CurrentLobby = CSteamID.Nil;
            return;
        }
        // Successfully requested join - OnLobbyEntered callback handles the actual entry logic.
        Debug.Log(
            $"[HasteTogether] Lobby join request for {lobbyId} successful. Waiting for entry confirmation..."
        );
    }

    // Callback for when Lobby Creation is confirmed (might be redundant with CallResult)
    private void OnLobbyCreated(LobbyCreated_t pCallback)
    {
        // This might fire in addition to the CallResult. Usually CallResult is sufficient.
        Debug.Log(
            $"[HasteTogether] OnLobbyCreated Callback: Result={pCallback.m_eResult}, LobbyID={pCallback.m_ulSteamIDLobby}"
        );
        // No logic needed here if OnLobbyCreatedResult handles everything.
    }

    // Callback for when Lobby Entry is confirmed
    private void OnLobbyEntered(LobbyEnter_t pCallback)
    {
        CSteamID lobbyId = new CSteamID(pCallback.m_ulSteamIDLobby);
        Debug.Log(
            $"[HasteTogether] OnLobbyEntered Callback: Response={(EChatRoomEnterResponse)pCallback.m_EChatRoomEnterResponse}, LobbyID={lobbyId}"
        );

        if (
            pCallback.m_EChatRoomEnterResponse
            != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess
        )
        {
            Debug.LogWarning(
                $"[HasteTogether] OnLobbyEntered callback reported non-success for {lobbyId}, ignoring."
            );
            return;
        }

        // If we were already in a lobby, leave it first
        if (IsInLobby && CurrentLobby != lobbyId)
        {
            Debug.LogWarning(
                $"[HasteTogether] Entered new lobby {lobbyId} while already in {CurrentLobby}. Leaving old lobby."
            );
            LeaveLobby(); // Leave the old one before proceeding
        }

        CurrentLobby = lobbyId;
        Peers.Clear();
        // Clear existing player objects if switching lobbies
        List<CSteamID> playersToRemove = Plugin.networkedPlayers.Keys.ToList();
        foreach (CSteamID steamId in playersToRemove)
        {
            Plugin.RemoveNetworkedPlayer(steamId);
        }
        Plugin.networkedPlayers.Clear();

        CSteamID lobbyOwner = SteamMatchmaking.GetLobbyOwner(CurrentLobby);
        string ownerName = SteamFriends.GetFriendPersonaName(lobbyOwner);
        Debug.Log(
            $"[HasteTogether] Entered lobby {CurrentLobby}. Owner: {ownerName} ({lobbyOwner})"
        );

        // Populate peer list and setup players
        int memberCount = SteamMatchmaking.GetNumLobbyMembers(CurrentLobby);
        Debug.Log($"[HasteTogether] Lobby Members ({memberCount}):");
        for (int i = 0; i < memberCount; i++)
        {
            CSteamID memberId = SteamMatchmaking.GetLobbyMemberByIndex(CurrentLobby, i);
            string memberName = SteamFriends.GetFriendPersonaName(memberId);
            Debug.Log($"  - {memberName} ({memberId})");
            Peers.Add(memberId);
            if (memberId != Plugin.LocalSteamId)
            {
                Plugin.GetOrSetupPlayer(memberId); // Use GetOrSetup to avoid duplicates
                // P2P Session acceptance is handled by OnP2PSessionRequest
            }
        }

        // Send our name to everyone else
        new NamePacket(SteamFriends.GetPersonaName()).Broadcast(SendType.Reliable);
        Debug.Log("[HasteTogether] Sent own name to lobby.");
    }

    // Callback for lobby member state changes (join, leave, disconnect)
    private void OnLobbyChatUpdate(LobbyChatUpdate_t pCallback)
    {
        CSteamID lobbyId = new CSteamID(pCallback.m_ulSteamIDLobby);
        if (lobbyId != CurrentLobby)
            return; // Update isn't for our current lobby

        CSteamID userChanged = new CSteamID(pCallback.m_ulSteamIDUserChanged);
        EChatMemberStateChange stateChange = (EChatMemberStateChange)
            pCallback.m_rgfChatMemberStateChange;
        string userName = SteamFriends.GetFriendPersonaName(userChanged);

        Debug.Log(
            $"[HasteTogether] Lobby Chat Update: User={userName}({userChanged}), Change={stateChange}"
        );

        if ((stateChange & EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
        {
            Debug.Log($"[HasteTogether] Member joined lobby: {userName} ({userChanged})");
            if (Peers.Add(userChanged)) // Add returns true if it was actually added (not already present)
            {
                Plugin.GetOrSetupPlayer(userChanged); // Setup player if newly added
                // Send our name specifically to the new person
                new NamePacket(SteamFriends.GetPersonaName()).SendTo(
                    userChanged,
                    SendType.Reliable
                );
            }
            // P2P Session should be handled by OnP2PSessionRequest
        }
        else if (
            (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeLeft) != 0
            || (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeDisconnected) != 0
            || (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeKicked) != 0
            || (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeBanned) != 0
        )
        {
            Debug.Log(
                $"[HasteTogether] Member left or disconnected: {userName} ({userChanged})"
            );
            if (Peers.Remove(userChanged))
            {
                Plugin.RemoveNetworkedPlayer(userChanged);
                SteamNetworking.CloseP2PSessionWithUser(userChanged); // Close P2P connection
            }

            // Check if we were the one who left/got kicked
            if (userChanged == Plugin.LocalSteamId)
            {
                Debug.Log("[HasteTogether] We are no longer in the lobby.");
                CleanupLobbyState(); // Clean up our own state
            }
            // Check if the host left
            else if (IsInLobby && userChanged == SteamMatchmaking.GetLobbyOwner(CurrentLobby))
            {
                Debug.LogWarning("[HasteTogether] Lobby host left!");
                // TODO: Implement host migration or simply leave
                LeaveLobby(); // Simple approach: everyone leaves
            }
        }
    }

    // Callback for changes in lobby metadata
    private void OnLobbyDataChanged(LobbyDataUpdate_t pCallback)
    {
        CSteamID lobbyId = new CSteamID(pCallback.m_ulSteamIDLobby);
        if (lobbyId != CurrentLobby)
            return;

        if (pCallback.m_bSuccess != 0) // Success means data changed
        {
            CSteamID memberId = new CSteamID(pCallback.m_ulSteamIDMember);
            if (memberId == CSteamID.Nil || memberId.m_SteamID == 0) // Lobby data changed
            {
                Debug.Log($"[HasteTogether] Lobby data changed for {lobbyId}");
                // Optionally enumerate all keys/values:
                int dataCount = SteamMatchmaking.GetLobbyDataCount(CurrentLobby);
                for (int i = 0; i < dataCount; i++)
                {
                    bool success = SteamMatchmaking.GetLobbyDataByIndex(
                        CurrentLobby,
                        i,
                        out string key,
                        256,
                        out string value,
                        256
                    );
                    if (success)
                        Debug.Log($"  Key='{key}', Value='{value}'");
                }
            }
            else // Member data changed (less common for simple P2P)
            {
                string memberName = SteamFriends.GetFriendPersonaName(memberId);
                Debug.Log($"[HasteTogether] Member data changed for {memberName} ({memberId})");
            }
        }
    }

    // Callback when another user wants to establish a P2P connection
    private void OnP2PSessionRequest(P2PSessionRequest_t pCallback)
    {
        CSteamID remoteId = pCallback.m_steamIDRemote;
        // Automatically accept connections from people in our lobby
        if (Peers.Contains(remoteId))
        {
            Debug.Log($"[HasteTogether] Accepting P2P session request from {remoteId}");
            SteamNetworking.AcceptP2PSessionWithUser(remoteId);
        }
        else
        {
            Debug.LogWarning(
                $"[HasteTogether] Ignoring P2P session request from non-peer {remoteId}"
            );
            // Optionally close session immediately if not a peer?
            // SteamNetworking.CloseP2PSessionWithUser(remoteId);
        }
    }

    // Callback when a P2P connection fails (e.g., NAT traversal issues)
    private void OnP2PSessionConnectFail(P2PSessionConnectFail_t pCallback)
    {
        CSteamID remoteId = pCallback.m_steamIDRemote;
        EP2PSessionError error = (EP2PSessionError)pCallback.m_eP2PSessionError;
        Debug.LogError($"[HasteTogether] P2P connection failed with {remoteId}. Error: {error}");
        // Maybe remove the peer or notify the user
        if (Peers.Remove(remoteId))
        {
            Plugin.RemoveNetworkedPlayer(remoteId);
        }
    }

    // --- Public Lobby List Handling ---
    public void RequestPublicLobbyList()
    {
        Debug.Log("[HasteTogether] Requesting public lobby list from Steam...");
        // Add filters if needed, e.g., filter by game version or specific tags
        // SteamMatchmaking.AddRequestLobbyListStringFilter("HasteTogetherVersion", "P2P_SWNET_1.0", ELobbyComparison.k_ELobbyComparisonEqual);
        SteamAPICall_t call = SteamMatchmaking.RequestLobbyList();
        m_LobbyMatchListCallResult.Set(call);
    }

    private void OnLobbyMatchList(LobbyMatchList_t result, bool ioFailure)
    {
        if (ioFailure)
        {
            Debug.LogError("[HasteTogether] Failed to get lobby list (IO failure).");
            Plugin.UpdatePublicLobbyList(new List<PublicLobbyInfo>()); // Send empty list on failure
            return;
        }

        List<PublicLobbyInfo> lobbyInfos = new List<PublicLobbyInfo>();
        int count = (int)result.m_nLobbiesMatching; // Use the count from the result
        Debug.Log($"[HasteTogether] Received lobby list. Found {count} lobbies.");

        for (int i = 0; i < count; i++)
        {
            CSteamID lobbyId = SteamMatchmaking.GetLobbyByIndex(i);
            if (!lobbyId.IsValid())
                continue; // Skip invalid lobbies

            // Retrieve lobby data - use "LobbyName" key set during creation
            string lobbyName = SteamMatchmaking.GetLobbyData(lobbyId, "LobbyName");
            if (string.IsNullOrEmpty(lobbyName))
            {
                // Fallback: Use host's name if LobbyName data is missing
                CSteamID ownerId = SteamMatchmaking.GetLobbyOwner(lobbyId);
                lobbyName = SteamFriends.GetFriendPersonaName(ownerId) + "'s Lobby";
            }

            // You could add more data here, like player count:
            // int currentPlayers = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
            // int maxPlayers = SteamMatchmaking.GetLobbyMemberLimit(lobbyId);
            // lobbyName += $" ({currentPlayers}/{maxPlayers})";

            lobbyInfos.Add(new PublicLobbyInfo { LobbyID = lobbyId, LobbyName = lobbyName });
        }
        // Update the Plugin's static list and UI
        Plugin.UpdatePublicLobbyList(lobbyInfos);
    }

    // --- P2P Packet Handling ---
    public void ReceiveP2PPackets()
    {
        if (!IsInLobby)
            return;

        uint msgSize;
        // Process all available packets on channel 0.
        while (SteamNetworking.IsP2PPacketAvailable(out msgSize, 0))
        {
            // Allocate buffer for the packet. Reuse buffer if possible?
            byte[] buffer = new byte[msgSize];
            CSteamID remoteId;
            uint bytesRead;

            // Read the packet into the buffer.
            if (
                SteamNetworking.ReadP2PPacket(buffer, msgSize, out bytesRead, out remoteId, 0)
                && bytesRead > 0
            )
            {
                // Only process packets from known peers in the lobby.
                if (Peers.Contains(remoteId))
                {
                    try
                    {
                        // Pass the exact data read
                        ProcessPacketData(remoteId, buffer);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError(
                            $"[HasteTogether] Exception processing P2P packet from {remoteId}: {e}"
                        );
                    }
                }
                else
                {
                    Debug.LogWarning(
                        $"[HasteTogether] Received P2P packet from unknown sender: {remoteId}. Ignoring."
                    );
                }
            }
            else
            {
                // This might happen if the packet disappears between IsAvailable and Read
                // Debug.LogWarning($"[HasteTogether] Failed to read available P2P packet (Size: {msgSize}).");
            }
        }
    }

    private void ProcessPacketData(CSteamID sourceSteamId, byte[] packetData)
    {
        if (packetData == null || packetData.Length == 0)
        {
            Debug.LogWarning(
                $"[HasteTogether] Received empty packet data from {sourceSteamId}."
            );
            return;
        }

        NetworkedPlayer? plr = null;
        byte packetId = packetData[0]; // First byte is the packet ID

        try
        {
            switch (packetId)
            {
                case 0x01: // Player Transform Update
                    if (packetData.Length < 16) // 1 byte ID + 15 bytes data
                    {
                        Debug.LogWarning(
                            $"[HasteTogether] Received malformed Transform packet (ID 0x01) from {sourceSteamId}. Length: {packetData.Length}"
                        );
                        break;
                    }
                    plr = Plugin.GetOrSetupPlayer(sourceSteamId);
                    if (plr == null)
                        break;

                    byte[] rawTransform = new byte[15];
                    Buffer.BlockCopy(packetData, 1, rawTransform, 0, 15);
                    plr.ApplyTransform(rawTransform);
                    break;

                case 0x02: // Player Name Update
                    if (packetData.Length < 2) // 1 byte ID + at least 1 byte name
                    {
                        Debug.LogWarning(
                            $"[HasteTogether] Received malformed Name packet (ID 0x02) from {sourceSteamId}. Length: {packetData.Length}"
                        );
                        break;
                    }
                    plr = Plugin.GetOrSetupPlayer(sourceSteamId);
                    if (plr == null)
                        break;

                    string receivedName = Encoding.UTF8.GetString(
                        packetData,
                        1,
                        packetData.Length - 1
                    );
                    plr.playerName = receivedName;
                    Debug.Log(
                        $"[HasteTogether] Received name for {sourceSteamId}: {plr.playerName}"
                    );
                    break;

                case 0x03: // Animation Packet Update (Changed ID)
                    if (packetData.Length < 2) // Must contain at least the animation type + key length
                    {
                        Debug.LogWarning(
                            $"[HasteTogether] Received malformed Animation packet (ID 0x03) from {sourceSteamId}. Length: {packetData.Length}"
                        );
                        break;
                    }
                    // Extract animation packet data (excluding the packet ID)
                    byte[] animData = new byte[packetData.Length - 1];
                    Buffer.BlockCopy(packetData, 1, animData, 0, animData.Length);

                    AnimationPacket animPacket = AnimationPacket.Deserialize(animData);

                    plr = Plugin.GetOrSetupPlayer(sourceSteamId);
                    if (plr == null || plr.animator == null)
                    {
                        Debug.LogWarning(
                            $"[HasteTogether] Received animation packet for player {sourceSteamId} but player or animator is missing."
                        );
                        break;
                    }

                    Animator animator = plr.animator;
                    switch (animPacket.Type)
                    {
                        case AnimationPacket.SetTypes.Play:
                            animator.Play(animPacket.AnimationKey);
                            // Debug.Log($"[HasteTogether] Animation Play: {animPacket.AnimationKey} for {sourceSteamId}");
                            break;
                        case AnimationPacket.SetTypes.SetInteger:
                            animator.SetInteger(
                                animPacket.AnimationKey,
                                Convert.ToInt32(animPacket.Value)
                            );
                            // Debug.Log($"[HasteTogether] Animation SetInteger: {animPacket.AnimationKey} = {animPacket.Value} for {sourceSteamId}");
                            break;
                        case AnimationPacket.SetTypes.SetBool:
                            animator.SetBool(
                                animPacket.AnimationKey,
                                Convert.ToBoolean(animPacket.Value)
                            );
                            // Debug.Log($"[HasteTogether] Animation SetBool: {animPacket.AnimationKey} = {animPacket.Value} for {sourceSteamId}");
                            break;
                        case AnimationPacket.SetTypes.SetFloat:
                            animator.SetFloat(
                                animPacket.AnimationKey,
                                Convert.ToSingle(animPacket.Value)
                            );
                            // Debug.Log($"[HasteTogether] Animation SetFloat: {animPacket.AnimationKey} = {animPacket.Value} for {sourceSteamId}");
                            break;
                        default:
                            Debug.LogWarning(
                                $"[HasteTogether] Unknown Animation type in packet from {sourceSteamId}"
                            );
                            break;
                    }
                    break;

                default:
                    Debug.LogWarning(
                        $"[HasteTogether] Received unknown packet ID: {packetId} from {sourceSteamId}"
                    );
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError(
                $"[HasteTogether] Error processing packet ID {packetId} from {sourceSteamId}: {ex}"
            );
        }
    }

    // Map internal SendType enum to Steamworks.NET EP2PSend enum
    private EP2PSend MapSendType(SendType sendType)
    {
        switch (sendType)
        {
            case SendType.Reliable:
                return EP2PSend.k_EP2PSendReliable;
            case SendType.Unreliable:
            default:
                return EP2PSend.k_EP2PSendUnreliableNoDelay;
        }
    }

    // Broadcasts data to all peers in the current lobby
    public void Broadcast(byte[] data, SendType sendType = SendType.Unreliable)
    {
        if (!IsInLobby || data == null || data.Length == 0)
            return;

        EP2PSend p2pSendType = MapSendType(sendType);
        int channel = 0;

        foreach (CSteamID peerId in Peers)
        {
            if (peerId != Plugin.LocalSteamId) // Don't send to self
            {
                bool success = SteamNetworking.SendP2PPacket(
                    peerId,
                    data,
                    (uint)data.Length,
                    p2pSendType,
                    channel
                );
                if (!success)
                {
                    Debug.LogWarning(
                        $"[HasteTogether] Failed to send P2P packet to peer {peerId} during broadcast."
                    );
                    // Consider checking P2P session state or removing peer after repeated failures
                }
            }
        }
    }

    // Sends data to a specific peer
    public void SendTo(CSteamID targetSteamId, byte[] data, SendType sendType = SendType.Unreliable)
    {
        if (
            !IsInLobby
            || targetSteamId == Plugin.LocalSteamId
            || !targetSteamId.IsValid()
            || data == null
            || data.Length == 0
        )
            return;

        if (!Peers.Contains(targetSteamId))
        {
            Debug.LogWarning(
                $"[HasteTogether] Attempted to send packet to non-peer {targetSteamId}. Ignoring."
            );
            return;
        }

        EP2PSend p2pSendType = MapSendType(sendType);
        int channel = 0;

        bool success = SteamNetworking.SendP2PPacket(
            targetSteamId,
            data,
            (uint)data.Length,
            p2pSendType,
            channel
        );

        if (!success)
        {
            Debug.LogError($"[HasteTogether] Failed to send P2P packet to {targetSteamId}.");
            // Consider checking P2P session state: SteamNetworking.GetP2PSessionState
        }
    }
}

// --- UI Component for Lobby Status ---
public class LobbyStatusUI : MonoBehaviour
{
    public UnityEngine.UI.Image img = null!;
    public Sprite inLobbySprite = null!;
    public Sprite notInLobbySprite = null!;
    public TextMeshProUGUI lobbyIdText = null!;

    void Update()
    {
        // Basic null checks
        if (
            Plugin.networkManager == null
            || img == null
            || inLobbySprite == null
            || notInLobbySprite == null
            || lobbyIdText == null
        )
        {
            if (lobbyIdText != null)
                lobbyIdText.enabled = false;
            if (img != null)
                img.enabled = false;
            return;
        }

        bool isInLobby = Plugin.networkManager.IsInLobby;
        img.sprite = isInLobby ? inLobbySprite : notInLobbySprite;
        img.enabled = true; // Ensure image is visible

        lobbyIdText.text = isInLobby
            ? $"Lobby: {Plugin.networkManager.CurrentLobby.m_SteamID}" // Display ulong ID
            : "Not in lobby";
        lobbyIdText.enabled = true; // Ensure text is visible
    }
}

// --- Networked Player Component ---
public class NetworkedPlayer : MonoBehaviour
{
    public CSteamID steamId;
    public Animator animator = null!;
    private Vector3 targetPosition = Vector3.zero;
    private Quaternion targetRotation = Quaternion.identity;
    private float interpolationSpeed = 15.0f; // Adjust for desired smoothness
    private float positionThresholdSqr = 0.0001f; // Threshold to snap position
    private float rotationThreshold = 0.5f; // Threshold to snap rotation (degrees)

    private string _playerName = "Loading...";
    public string playerName
    {
        get => _playerName;
        set
        {
            _playerName = value ?? "InvalidName";
            gameObject.name = $"HasteTogether_{_playerName}_{steamId}";
            if (playerNameText != null)
            {
                playerNameText.text = _playerName;
            }
            else
            {
                SetupPlayerUI(); // Attempt setup if UI wasn't ready
                if (playerNameText != null)
                    playerNameText.text = _playerName;
            }
        }
    }
    public Canvas playerCanvas = null!;
    public TextMeshProUGUI playerNameText = null!;

    void Awake()
    {
        targetPosition = transform.position;
        targetRotation = transform.rotation;
        SetupPlayerUI(); // Attempt UI setup immediately
        // Request name from Steam if not set yet (might be set by NamePacket later)
        if (_playerName == "Loading...")
        {
            playerName = SteamFriends.GetFriendPersonaName(steamId);
        }
    }

    // Apply received transform data (deserialization)
    public void ApplyTransform(byte[] transformData)
    {
        if (transformData == null || transformData.Length < 15)
        {
            Debug.LogWarning($"[NetworkedPlayer {steamId}] Received invalid transform data.");
            return;
        }
        // Position (24-bit ints -> float)
        int xR = (transformData[0] << 16) | (transformData[1] << 8) | transformData[2];
        int yR = (transformData[3] << 16) | (transformData[4] << 8) | transformData[5];
        int zR = (transformData[6] << 16) | (transformData[7] << 8) | transformData[8];
        targetPosition = new Vector3(
            (xR / 256.0f) - 32767.5f,
            (yR / 256.0f) - 32767.5f,
            (zR / 256.0f) - 32767.5f
        );
        // Rotation (24-bit ints -> float for y, w)
        int rY = (transformData[9] << 16) | (transformData[10] << 8) | transformData[11];
        int rW = (transformData[12] << 16) | (transformData[13] << 8) | transformData[14];
        // Convert back from [0, 16777215] to [-1, 1]
        float rotY = (rY / 8388607.5f) - 1.0f;
        float rotW = (rW / 8388607.5f) - 1.0f;
        // Reconstruct quaternion (assuming x/z are derived or 0)
        float y2 = rotY * rotY;
        float w2 = rotW * rotW;
        float xzSumSq = 1.0f - y2 - w2;
        float rotX = 0.0f;
        float rotZ = (xzSumSq < 0.0f) ? 0.0f : Mathf.Sqrt(xzSumSq);
        targetRotation = new Quaternion(rotX, rotY, rotZ, rotW).normalized;
    }

    private void Update()
    {
        // Interpolate position
        if ((transform.position - targetPosition).sqrMagnitude > positionThresholdSqr)
        {
            transform.position = Vector3.Lerp(
                transform.position,
                targetPosition,
                Time.deltaTime * interpolationSpeed
            );
        }
        else
        {
            transform.position = targetPosition; // Snap if close
        }

        // Interpolate rotation
        if (Quaternion.Angle(transform.rotation, targetRotation) > rotationThreshold)
        {
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                targetRotation,
                Time.deltaTime * interpolationSpeed
            );
        }
        else
        {
            transform.rotation = targetRotation; // Snap if close
        }

        // Make player name canvas face the camera
        if (playerCanvas != null && Camera.main != null)
        {
            playerCanvas.transform.LookAt(
                playerCanvas.transform.position + Camera.main.transform.rotation * Vector3.forward,
                Camera.main.transform.rotation * Vector3.up
            );
        }
    }

    // Sets up the in-world UI canvas and text for the player's name
    void SetupPlayerUI()
    {
        if (playerCanvas != null || !this.enabled)
            return; // Already setup or component disabled

        try
        {
            // Create Canvas GameObject
            GameObject canvasGO = new GameObject("PlayerCanvas");
            canvasGO.transform.SetParent(transform, false);

            playerCanvas = canvasGO.AddComponent<Canvas>();
            playerCanvas.renderMode = RenderMode.WorldSpace;
            playerCanvas.sortingOrder = 1;

            RectTransform canvasRect = canvasGO.GetComponent<RectTransform>();
            canvasRect.localPosition = new Vector3(0, 2.5f, 0); // Position above head
            canvasRect.sizeDelta = new Vector2(200, 50);
            canvasRect.localScale = new Vector3(0.01f, 0.01f, 0.01f); // Scale for world space

            // Create Text GameObject
            GameObject textGO = new GameObject("NametagText");
            textGO.transform.SetParent(playerCanvas.transform, false);

            playerNameText = textGO.AddComponent<TextMeshProUGUI>();
            playerNameText.text = playerName; // Set initial text
            playerNameText.fontSize = 24;
            playerNameText.color = UnityEngine.Color.white;
            playerNameText.alignment = TextAlignmentOptions.Center;
            playerNameText.enableWordWrapping = false;
            playerNameText.overflowMode = TextOverflowModes.Overflow;
            playerNameText.fontStyle = FontStyles.Bold;

            // Assign default font
            if (TMP_Settings.defaultFontAsset != null)
                playerNameText.font = TMP_Settings.defaultFontAsset;
            else
                Debug.LogWarning(
                    "[HasteTogether] TMP Default Font Asset is null. Nametag may not render."
                );

            RectTransform textRect = textGO.GetComponent<RectTransform>();
            textRect.localPosition = Vector3.zero;
            textRect.sizeDelta = canvasRect.sizeDelta;
            textRect.localScale = Vector3.one;

            playerNameText.ForceMeshUpdate(); // Update mesh

            Debug.Log($"[HasteTogether] Setup UI for {steamId}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[HasteTogether] Error setting up player UI for {steamId}: {e}");
            if (playerCanvas != null)
                Destroy(playerCanvas.gameObject);
            playerCanvas = null!;
            playerNameText = null!;
        }
    }

    void OnDestroy()
    {
        if (playerCanvas != null)
        {
            Destroy(playerCanvas.gameObject);
        }
    }
}

// --- Packets ---
// (Packet base class, UpdatePacket, NamePacket, AnimationPacket definitions go here - provided in previous response)
// --- Packets ---
// Base Packet class remains mostly the same, just update Send methods
public abstract class Packet
{
    public abstract byte PacketID();
    public abstract byte[] Serialize();

    // Helper to map internal enum to Steamworks enum
    protected EP2PSend MapSteamSendType(SendType sendType)
    {
        switch (sendType)
        {
            case SendType.Reliable:
                return EP2PSend.k_EP2PSendReliable;
            case SendType.Unreliable:
            default:
                return EP2PSend.k_EP2PSendUnreliableNoDelay; // Good default for game data
        }
    }

    // Prepares the full byte array with Packet ID prefix
    protected byte[] GetBytesToSend()
    {
        byte[] data = Serialize();
        byte[] toSend = new byte[data.Length + 1];
        toSend[0] = PacketID(); // Add packet ID header
        Buffer.BlockCopy(data, 0, toSend, 1, data.Length);
        return toSend;
    }

    public void Broadcast(SendType sendType = SendType.Unreliable)
    {
        if (Plugin.networkManager == null || !Plugin.networkManager.IsInLobby)
            return;

        byte[] toSend = GetBytesToSend();
        Plugin.networkManager.Broadcast(toSend, sendType); // Use NetworkManager's broadcast
    }

    public void SendTo(CSteamID targetSteamId, SendType sendType = SendType.Unreliable) // Use CSteamID
    {
        if (Plugin.networkManager == null || !Plugin.networkManager.IsInLobby)
            return;

        byte[] toSend = GetBytesToSend();
        Plugin.networkManager.SendTo(targetSteamId, toSend, sendType); // Use NetworkManager's SendTo
    }
}

// UpdatePacket remains the same internally, relies on base class Send methods
public class UpdatePacket : Packet
{
    public override byte PacketID() => 0x01;

    private Vector3 position;
    private Quaternion rotation;

    // Serialization uses the static Plugin method, which is unchanged
    public override byte[] Serialize() => Plugin.SerializeTransform(position, rotation);

    // Comparison logic remains the same
    public bool HasChanged(
        UpdatePacket previousPacket,
        float posThresholdSqr = 0.0001f, // Use squared threshold for position
        float rotThreshold = 0.1f
    )
    {
        if (previousPacket == null)
            return true;
        // Compare position using squared magnitude for efficiency
        if ((position - previousPacket.position).sqrMagnitude > posThresholdSqr)
            return true;
        // Compare rotation using Quaternion.Angle
        if (Quaternion.Angle(rotation, previousPacket.rotation) > rotThreshold)
            return true;
        return false;
    }

    // Constructor logic remains the same
    public UpdatePacket(Transform player)
    {
        this.position = player.position;
        // Attempt to get visual rotation if component exists
        var visualRotation = player.GetComponent<PlayerVisualRotation>(); // Assuming this component exists
        this.rotation = visualRotation != null ? visualRotation.visual.rotation : player.rotation;
    }

    // Default constructor remains the same
    public UpdatePacket()
    {
        this.position = Vector3.zero;
        this.rotation = Quaternion.identity;
    }
}

// NamePacket remains the same internally, relies on base class Send methods
public class NamePacket : Packet
{
    private string name;

    public override byte PacketID() => 0x02; // ID is 0x02

    // Serialization uses UTF8 encoding, which is standard
    public override byte[] Serialize() => Encoding.UTF8.GetBytes(name);

    // Constructor ensures name is not null
    public NamePacket(string name)
    {
        this.name = name ?? "UnknownPlayer"; // Provide a default if null
    }
}

// AnimationPacket definition
public class AnimationPacket : Packet
{
    public enum SetTypes : byte
    {
        Play = 0x00,
        SetInteger = 0x01,
        SetBool = 0x02,
        SetFloat = 0x03,
    }

    public SetTypes Type;
    public string AnimationKey; // For Play, this is the state name
    public object Value; // For properties (integers, bools, floats)

    public AnimationPacket(SetTypes type, string key, object value)
    {
        Type = type;
        AnimationKey = key;
        Value = value;
    }

    public override byte PacketID() => 0x03; // ID is 0x03

    public override byte[] Serialize()
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(AnimationKey);
        byte keyLength = (byte)keyBytes.Length;
        byte[] valueBytes;

        switch (Type)
        {
            case SetTypes.SetInteger:
                valueBytes = BitConverter.GetBytes(Convert.ToInt32(Value));
                break;
            case SetTypes.SetBool:
                valueBytes = new byte[] { Convert.ToBoolean(Value) ? (byte)1 : (byte)0 };
                break;
            case SetTypes.SetFloat:
                valueBytes = BitConverter.GetBytes(Convert.ToSingle(Value));
                break;
            case SetTypes.Play:
            default:
                valueBytes = new byte[0];
                break;
        }

        byte[] packet = new byte[1 + 1 + keyBytes.Length + valueBytes.Length];
        packet[0] = (byte)Type;
        packet[1] = keyLength;
        Buffer.BlockCopy(keyBytes, 0, packet, 2, keyBytes.Length);
        if (valueBytes.Length > 0)
            Buffer.BlockCopy(valueBytes, 0, packet, 2 + keyBytes.Length, valueBytes.Length);
        return packet;
    }

    public static AnimationPacket Deserialize(byte[] data)
    {
        if (data.Length < 2)
            throw new Exception("Invalid AnimationPacket: insufficient data.");
        SetTypes type = (SetTypes)data[0];
        byte keyLength = data[1];
        if (data.Length < 2 + keyLength)
            throw new Exception("Invalid AnimationPacket: incomplete key.");
        string key = Encoding.UTF8.GetString(data, 2, keyLength);
        object value = null;
        int valueIndex = 2 + keyLength;
        switch (type)
        {
            case SetTypes.SetInteger:
                if (data.Length < valueIndex + 4)
                    throw new Exception("Invalid AnimationPacket: insufficient integer data.");
                value = BitConverter.ToInt32(data, valueIndex);
                break;
            case SetTypes.SetBool:
                if (data.Length < valueIndex + 1)
                    throw new Exception("Invalid AnimationPacket: insufficient bool data.");
                value = data[valueIndex] == 1;
                break;
            case SetTypes.SetFloat:
                if (data.Length < valueIndex + 4)
                    throw new Exception("Invalid AnimationPacket: insufficient float data.");
                value = BitConverter.ToSingle(data, valueIndex);
                break;
        }
        return new AnimationPacket(type, key, value);
    }

    public override bool Equals(object obj)
    {
        if (!(obj is AnimationPacket other))
            return false;
        if (Type != other.Type || !AnimationKey.Equals(other.AnimationKey))
            return false;
        if (Type == SetTypes.SetFloat)
            return Math.Abs(Convert.ToSingle(Value) - Convert.ToSingle(other.Value)) < 0.01f;
        return Equals(Value, other.Value);
    }

    public override int GetHashCode() => HashCode.Combine(Type, AnimationKey, Value);
}

// --- HookManager (Using MonoMod) ---
public static class HookManager
{
    // Delegate/Hook fields for PlayerCharacter.Update, PersistentObjects.Awake, SteamAPI.RestartAppIfNecessary
    private delegate void PlayerCharacter_UpdateDelegate(PlayerCharacter self);
    private static Hook? hookPlayerUpdate;
    private static PlayerCharacter_UpdateDelegate? orig_PlayerCharacter_Update;

    private delegate void PersistentObjects_AwakeDelegate(PersistentObjects self);
    private static Hook? hookPersistentAwake;
    private static PersistentObjects_AwakeDelegate? orig_PersistentObjects_Awake;

    private delegate bool RestartAppDelegate(AppId_t appId);
    private static Hook? hookRestartApp;
    private static RestartAppDelegate? orig_RestartApp;

    // Delegate/Hook fields for Animator methods
    private delegate void Animator_PlayDelegate(
        Animator self,
        string stateName,
        int layer,
        float normalizedTime
    );
    private static Hook? hookAnimatorPlay;
    private static Animator_PlayDelegate? orig_Animator_Play;

    private delegate void Animator_SetIntegerDelegate(Animator self, string name, int value);
    private static Hook? hookAnimatorSetInteger;
    private static Animator_SetIntegerDelegate? orig_Animator_SetInteger;

    private delegate void Animator_SetBoolDelegate(Animator self, string name, bool value);
    private static Hook? hookAnimatorSetBool;
    private static Animator_SetBoolDelegate? orig_Animator_SetBool;

    private delegate void Animator_SetFloatDelegate(Animator self, string name, float value);
    private static Hook? hookAnimatorSetFloat;
    private static Animator_SetFloatDelegate? orig_Animator_SetFloat;

    // Helper to check if an animator belongs to the local player
    private static bool IsLocalPlayerAnimator(Animator animator)
    {
        // TODO: Implement your actual check here. Example:
        // PlayerCharacter localPlayer = PlayerCharacter.localPlayer; // Or however you get the local player instance
        // return localPlayer != null && animator == localPlayer.refs?.animationHandler?.animator;
        return true; // Placeholder - REMOVE THIS AND IMPLEMENT PROPER CHECK
    }

    // Detour methods for Animator hooks
    private static void Animator_PlayDetour(
        Animator self,
        string stateName,
        int layer,
        float normalizedTime
    )
    {
        orig_Animator_Play?.Invoke(self, stateName, layer, normalizedTime); // Call original first
        if (IsLocalPlayerAnimator(self))
        {
            // Use the static helper to send if changed
            Plugin.SendAnimationPacketIfNeeded(
                new AnimationPacket(AnimationPacket.SetTypes.Play, stateName, null) // Key is stateName for Play
            );
        }
    }

    private static void Animator_SetIntegerDetour(Animator self, string name, int value)
    {
        orig_Animator_SetInteger?.Invoke(self, name, value);
        if (IsLocalPlayerAnimator(self))
        {
            Plugin.SendAnimationPacketIfNeeded(
                new AnimationPacket(AnimationPacket.SetTypes.SetInteger, name, value)
            );
        }
    }

    private static void Animator_SetBoolDetour(Animator self, string name, bool value)
    {
        orig_Animator_SetBool?.Invoke(self, name, value);
        if (IsLocalPlayerAnimator(self))
        {
            Plugin.SendAnimationPacketIfNeeded(
                new AnimationPacket(AnimationPacket.SetTypes.SetBool, name, value)
            );
        }
    }

    private static void Animator_SetFloatDetour(Animator self, string name, float value)
    {
        orig_Animator_SetFloat?.Invoke(self, name, value);
        if (IsLocalPlayerAnimator(self))
        {
            Plugin.SendAnimationPacketIfNeeded(
                new AnimationPacket(AnimationPacket.SetTypes.SetFloat, name, value)
            );
        }
    }

    // Installs all hooks
    public static void InstallHooks()
    {
        Debug.Log("[HasteTogether] Installing hooks...");
        InstallHook(
            "PlayerCharacter.Update",
            typeof(PlayerCharacter),
            "Update",
            ref hookPlayerUpdate,
            ref orig_PlayerCharacter_Update,
            PlayerCharacter_UpdateDetour
        );
        InstallHook(
            "PersistentObjects.Awake",
            typeof(PersistentObjects),
            "Awake",
            ref hookPersistentAwake,
            ref orig_PersistentObjects_Awake,
            PersistentObjects_AwakeDetour
        );
        InstallHook(
            "SteamAPI.RestartAppIfNecessary",
            typeof(SteamAPI),
            "RestartAppIfNecessary",
            ref hookRestartApp,
            ref orig_RestartApp,
            RestartAppDetour,
            BindingFlags.Static | BindingFlags.Public
        );

        // Install Animator Hooks
        InstallHook(
            "Animator.Play",
            typeof(Animator),
            "Play",
            ref hookAnimatorPlay,
            ref orig_Animator_Play,
            Animator_PlayDetour,
            BindingFlags.Instance | BindingFlags.Public,
            new Type[] { typeof(string), typeof(int), typeof(float) }
        );
        InstallHook(
            "Animator.SetInteger",
            typeof(Animator),
            "SetInteger",
            ref hookAnimatorSetInteger,
            ref orig_Animator_SetInteger,
            Animator_SetIntegerDetour,
            BindingFlags.Instance | BindingFlags.Public,
            new Type[] { typeof(string), typeof(int) }
        );
        InstallHook(
            "Animator.SetBool",
            typeof(Animator),
            "SetBool",
            ref hookAnimatorSetBool,
            ref orig_Animator_SetBool,
            Animator_SetBoolDetour,
            BindingFlags.Instance | BindingFlags.Public,
            new Type[] { typeof(string), typeof(bool) }
        );
        InstallHook(
            "Animator.SetFloat",
            typeof(Animator),
            "SetFloat",
            ref hookAnimatorSetFloat,
            ref orig_Animator_SetFloat,
            Animator_SetFloatDetour,
            BindingFlags.Instance | BindingFlags.Public,
            new Type[] { typeof(string), typeof(float) }
        );
    }

    // Generic Hook Installation Helper
    private static void InstallHook<TDelegate>(
        string hookName,
        Type targetType,
        string methodName,
        ref Hook? hookField,
        ref TDelegate? origField,
        TDelegate detourDelegate,
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        Type[]? parameterTypes = null
    )
        where TDelegate : Delegate
    {
        MethodInfo? methodInfo =
            parameterTypes == null
                ? targetType.GetMethod(methodName, flags)
                : targetType.GetMethod(methodName, flags, null, parameterTypes, null);

        if (methodInfo != null)
        {
            try
            {
                hookField = new Hook(methodInfo, detourDelegate);
                origField = hookField.GenerateTrampoline<TDelegate>();
                Debug.Log($"[HasteTogether] Hooked {hookName} successfully.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HasteTogether] Failed to hook {hookName}: {ex}");
            }
        }
        else
        {
            Debug.LogError(
                $"[HasteTogether] Could not find method for hook: {hookName} in {targetType.FullName}"
            );
        }
    }

    // --- Detour Methods ---
    private static void PlayerCharacter_UpdateDetour(PlayerCharacter self)
    {
        orig_PlayerCharacter_Update?.Invoke(self); // Call original first
        try
        {
            // Send position updates if in a lobby and local player
            if (
                Plugin.networkManager != null
                && Plugin.networkManager.IsInLobby /*&& self.IsLocalPlayer*/
            ) // Assuming IsLocalPlayer exists
            {
                UpdatePacket packet = new UpdatePacket(self.transform);
                if (packet.HasChanged(Plugin.lastSent, 0.0001f, 0.1f))
                {
                    packet.Broadcast(SendType.Unreliable);
                    Plugin.lastSent = packet;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[HasteTogether] Error in PlayerCharacter_UpdateDetour: {ex}");
        }
    }

    private static void PersistentObjects_AwakeDetour(PersistentObjects self)
    {
        Debug.Log("[HasteTogether] PersistentObjects_AwakeDetour called.");
        orig_PersistentObjects_Awake?.Invoke(self); // Call original first
        Debug.Log("[HasteTogether] Original PersistentObjects.Awake finished.");

        // --- UI Setup Logic ---
        if (self == null || self.gameObject == null)
        {
            Debug.LogWarning(
                "[HasteTogether] PersistentObjects instance is null after Awake. Skipping UI setup."
            );
            return;
        }
        Transform? persistentUIRoot = self.transform.Find("UI_Persistent");
        if (persistentUIRoot == null)
        {
            Debug.LogWarning(
                "[HasteTogether] 'UI_Persistent' not found under PersistentObjects. Creating..."
            );
            // Create basic UI root if missing (adapt as needed for the game's UI structure)
            GameObject uiRootObj = new GameObject("UI_Persistent");
            persistentUIRoot = uiRootObj.transform;
            persistentUIRoot.SetParent(self.transform, false);
            Canvas rootCanvas =
                uiRootObj.GetComponent<Canvas>() ?? uiRootObj.AddComponent<Canvas>();
            rootCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            if (uiRootObj.GetComponent<CanvasScaler>() == null)
                uiRootObj.AddComponent<CanvasScaler>();
            if (uiRootObj.GetComponent<GraphicRaycaster>() == null)
                uiRootObj.AddComponent<GraphicRaycaster>();
        }

        if (Plugin.TogetherUI == null && persistentUIRoot != null)
        {
            try
            {
                Plugin.TogetherUI = new GameObject("TogetherP2P_UI").transform;
                Plugin.TogetherUI.SetParent(persistentUIRoot, false);
                Plugin.TogetherUI.localPosition = Vector3.zero;
                Plugin.TogetherUI.localScale = Vector3.one;
                Debug.Log("[HasteTogether] Created TogetherP2P_UI root under UI_Persistent.");

                // Create Lobby Status Image
                GameObject statusImgObj = new GameObject("LobbyStatusImage");
                statusImgObj.transform.SetParent(Plugin.TogetherUI, false);
                Image statusImage = statusImgObj.AddComponent<Image>();
                RectTransform imgRect = statusImgObj.GetComponent<RectTransform>();
                imgRect.anchorMin = new Vector2(1, 1);
                imgRect.anchorMax = new Vector2(1, 1);
                imgRect.pivot = new Vector2(1, 1);
                imgRect.anchoredPosition = new Vector2(-20, -20); // Top-right corner offset
                imgRect.sizeDelta = new Vector2(64, 64);
                statusImage.preserveAspect = true;

                LobbyStatusUI statusUI = statusImgObj.AddComponent<LobbyStatusUI>();
                statusUI.img = statusImage;
                // Load sprites (ensure resource names are correct and embedded)
                statusUI.inLobbySprite = LoadSpriteFromResource(
                    "HasteTogether.P2P.Graphics.HasteTogether_Connected.png"
                );
                statusUI.notInLobbySprite = LoadSpriteFromResource(
                    "HasteTogether.P2P.Graphics.HasteTogether_Disconnected.png"
                );
                if (statusUI.inLobbySprite == null || statusUI.notInLobbySprite == null)
                    Debug.LogError("[HasteTogether] Failed to load status sprites!");

                // Create Lobby ID Text
                GameObject lobbyIdObj = new GameObject("LobbyIdText");
                lobbyIdObj.transform.SetParent(Plugin.TogetherUI, false);
                TextMeshProUGUI lobbyText = lobbyIdObj.AddComponent<TextMeshProUGUI>();
                RectTransform textRect = lobbyIdObj.GetComponent<RectTransform>();
                textRect.anchorMin = new Vector2(1, 1);
                textRect.anchorMax = new Vector2(1, 1);
                textRect.pivot = new Vector2(1, 1);
                textRect.anchoredPosition = new Vector2(-100, -25); // Near image
                textRect.sizeDelta = new Vector2(300, 30);
                lobbyText.fontSize = 14;
                lobbyText.color = Color.white;
                lobbyText.alignment = TextAlignmentOptions.TopRight;
                lobbyText.text = "Initializing...";
                if (TMP_Settings.defaultFontAsset != null)
                    lobbyText.font = TMP_Settings.defaultFontAsset;
                statusUI.lobbyIdText = lobbyText;

                Debug.Log("[HasteTogether] Persistent UI setup attempt complete.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HasteTogether] Error setting up TogetherP2P_UI: {ex}");
                if (Plugin.TogetherUI != null)
                    GameObject.Destroy(Plugin.TogetherUI.gameObject);
                Plugin.TogetherUI = null!;
            }
        }
        else if (Plugin.TogetherUI != null)
            Debug.LogWarning("[HasteTogether] TogetherP2P_UI already exists.");
        else
            Debug.LogError(
                "[HasteTogether] Cannot setup UI - Persistent UI Root not found/created."
            );
    }

    private static bool RestartAppDetour(AppId_t appId)
    {
        Debug.LogWarning(
            $"[HasteTogether] SteamAPI.RestartAppIfNecessary called for AppId: {appId}. Preventing restart."
        );
        return false; // Prevent the restart
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
                Debug.LogError(
                    $"[HasteTogether] Embedded resource not found: {resourceName}. Available: {available}"
                );
                return null;
            }
            using MemoryStream ms = new MemoryStream();
            imgStream.CopyTo(ms);
            byte[] buffer = ms.ToArray();
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point, // Good for pixel art UI
                wrapMode = TextureWrapMode.Clamp,
            };
            if (texture.LoadImage(buffer))
            {
                Sprite sprite = Sprite.Create(
                    texture,
                    new Rect(0, 0, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f),
                    100.0f
                );
                sprite.name = resourceName;
                return sprite;
            }
            else
            {
                Debug.LogError(
                    $"[HasteTogether] Failed to load image data from resource: {resourceName}"
                );
                GameObject.Destroy(texture);
                return null;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError(
                $"[HasteTogether] Exception loading sprite from resource '{resourceName}': {ex}"
            );
            return null;
        }
    }
}

// --- Settings Classes ---
// (Includes LobbyCreationOptionsSetting, JoinLobbySettingGroup, PublicLobbyListSetting and their children)
// --- Settings Classes ---

// --- Lobby Creation Settings ---
public enum LobbyCreationType
{
    Private,
    Public,
}

[HasteSetting]
public class LobbyCreationOptionsSetting : CollapsibleSetting, IExposedSetting
{
    public LobbyCreationTypeSetting LobbyType = new LobbyCreationTypeSetting();
    public PublicLobbyNameField LobbyName = new PublicLobbyNameField("Lobby Name", "");
    public PublicLobbyCreateButton CreateLobbyButton = new PublicLobbyCreateButton();

    public LobbyCreationOptionsSetting()
    {
        CreateLobbyButton.LobbyNameField = LobbyName;
        CreateLobbyButton.LobbyTypeSetting = LobbyType;
    }

    public override List<Setting> GetSettings() =>
        new List<Setting> { LobbyType, LobbyName, CreateLobbyButton };

    public string GetCategory() => "Multiplayer";

    public LocalizedString GetDisplayName() => new UnlocalizedString("Create Lobby Options");
}

public class LobbyCreationTypeSetting : EnumSetting<LobbyCreationType>, IExposedSetting
{
    public override void ApplyValue() { }

    public override List<LocalizedString> GetLocalizedChoices() =>
        new List<LocalizedString>
        {
            new UnlocalizedString("Private"),
            new UnlocalizedString("Public"),
        };

    public string GetCategory() => "Multiplayer";

    public LocalizedString GetDisplayName() => new UnlocalizedString("Lobby Type");

    protected override LobbyCreationType GetDefaultValue() => LobbyCreationType.Private;
}

public class PublicLobbyNameField : StringSetting, IExposedSetting
{
    private readonly string keyName;
    private readonly string defaultValue;

    public PublicLobbyNameField(string key, string defaultValue)
    {
        keyName = key;
        this.defaultValue = defaultValue;
    }

    public override void ApplyValue() { }

    public string GetCategory() => "Multiplayer";

    public LocalizedString GetDisplayName() => new UnlocalizedString(keyName);

    protected override string GetDefaultValue() => defaultValue;
}

public class PublicLobbyCreateButton : ButtonSetting, IExposedSetting
{
    public PublicLobbyNameField? LobbyNameField;
    public LobbyCreationTypeSetting? LobbyTypeSetting;

    public override void ApplyValue() { }

    public override string GetButtonText() => "Create Lobby";

    public string GetCategory() => "Multiplayer";

    public LocalizedString GetDisplayName() => new UnlocalizedString("Create Lobby");

    public override void OnClicked(ISettingHandler settingHandler)
    {
        if (LobbyTypeSetting == null)
        {
            Debug.LogError("[HasteTogether] LobbyTypeSetting is null.");
            return;
        }
        if (Plugin.networkManager == null)
        {
            Debug.LogError("[HasteTogether] NetworkManager not initialized.");
            return;
        }
        if (LobbyTypeSetting.Value == LobbyCreationType.Private)
            Plugin.CreatePrivateLobbyCommand();
        else
        {
            if (LobbyNameField == null)
            {
                Debug.LogError("[HasteTogether] LobbyNameField is null.");
                return;
            }
            string lobbyName = LobbyNameField.Value;
            if (string.IsNullOrWhiteSpace(lobbyName))
            {
                Debug.LogWarning("[HasteTogether] Public lobby name is empty.");
                return;
            }
            Plugin.CreatePublicLobbyCommand(lobbyName);
        }
    }
}

// --- Lobby Joining Settings ---
[HasteSetting]
public class JoinLobbySettingGroup : CollapsibleSetting, IExposedSetting
{
    public PrivateLobbyJoinGroup PrivateJoin = new PrivateLobbyJoinGroup();
    public PublicLobbyListSetting PublicLobbies = new PublicLobbyListSetting();

    public override List<Setting> GetSettings() => new List<Setting> { PrivateJoin, PublicLobbies };

    public string GetCategory() => "Multiplayer";

    public LocalizedString GetDisplayName() => new UnlocalizedString("Join Lobby");
    // No custom UI cell needed if auto-refresh on expand is not required
}

public class PrivateLobbyJoinGroup : CollapsibleSetting, IExposedSetting
{
    public PrivateCodeInputField CodeInput = new PrivateCodeInputField("Lobby Code", "");
    public PrivateCodeJoinButton JoinButton = new PrivateCodeJoinButton();

    public PrivateLobbyJoinGroup()
    {
        JoinButton.CodeInputField = CodeInput;
    }

    public override List<Setting> GetSettings() => new List<Setting> { CodeInput, JoinButton };

    public string GetCategory() => "Multiplayer";

    public LocalizedString GetDisplayName() => new UnlocalizedString("Join via Private Code");
}

public class PrivateCodeInputField : StringSetting, IExposedSetting
{
    private readonly string keyName;
    private readonly string defaultValue;

    public PrivateCodeInputField(string key, string defaultValue)
    {
        keyName = key;
        this.defaultValue = defaultValue;
    }

    public override void ApplyValue() { }

    public string GetCategory() => "Multiplayer";

    public LocalizedString GetDisplayName() => new UnlocalizedString(keyName);

    protected override string GetDefaultValue() => defaultValue;
}

public class PrivateCodeJoinButton : ButtonSetting, IExposedSetting
{
    public PrivateCodeInputField? CodeInputField;

    public override void ApplyValue() { }

    public override string GetButtonText() => "Join Private Lobby";

    public string GetCategory() => "Multiplayer";

    public LocalizedString GetDisplayName() => new UnlocalizedString("Join");

    public override void OnClicked(ISettingHandler settingHandler)
    {
        if (CodeInputField == null || string.IsNullOrWhiteSpace(CodeInputField.Value))
        {
            Debug.LogWarning("[HasteTogether] Private lobby code is empty.");
            return;
        }
        if (Plugin.networkManager == null)
        {
            Debug.LogError("[HasteTogether] NetworkManager not initialized.");
            return;
        }
        Plugin.JoinLobbyCommand(CodeInputField.Value);
    }
}

// --- Public Lobby List ---
public struct PublicLobbyInfo
{
    public CSteamID LobbyID;
    public string LobbyName;
};

public class PublicLobbyListSetting : DynamicSettingList, IExposedSetting
{
    public RefreshPublicLobbyListButton RefreshButton = new RefreshPublicLobbyListButton();

    public PublicLobbyListSetting()
    {
        Plugin.PublicLobbyListSettingInstance = this;
        Settings.Add(RefreshButton); // Add refresh button permanently
    }

    public void UpdateEntries(List<PublicLobbyInfo> lobbyInfos)
    {
        Settings.Clear();
        Settings.Add(RefreshButton); // Re-add refresh button
        foreach (var info in lobbyInfos)
            Settings.Add(new PublicLobbyEntrySetting(info));
        UpdateUI(); // Trigger base class UI update
        Debug.Log(
            $"[HasteTogether] Updated public lobby list setting. Total items: {Settings.Count}. UI refresh requested."
        );
    }

    public string GetCategory() => "Multiplayer";

    public LocalizedString GetDisplayName() => new UnlocalizedString("Public Lobbies");
}

public class PublicLobbyEntrySetting : CollapsibleSetting, IExposedSetting
{
    public string LobbyName;
    public string LobbyID;
    public LobbyJoinButton JoinButton = new LobbyJoinButton();

    public PublicLobbyEntrySetting(PublicLobbyInfo info)
    {
        LobbyName = info.LobbyName;
        LobbyID = info.LobbyID.m_SteamID.ToString();
        JoinButton.LobbyID = LobbyID;
    }

    public string GetCategory() => "Multiplayer";

    public LocalizedString GetDisplayName() => new UnlocalizedString($"{LobbyName} ({LobbyID})");

    public override List<Setting> GetSettings() => new List<Setting> { JoinButton };
}

public class LobbyJoinButton : ButtonSetting, IExposedSetting
{
    public string LobbyID = "";

    public override void ApplyValue() { }

    public override string GetButtonText() => "Join Lobby";

    public string GetCategory() => "Multiplayer";

    public LocalizedString GetDisplayName() => new UnlocalizedString("Join");

    public override void OnClicked(ISettingHandler settingHandler)
    {
        if (string.IsNullOrWhiteSpace(LobbyID))
        {
            Debug.LogWarning("[HasteTogether] Lobby ID is empty.");
            return;
        }
        if (Plugin.networkManager == null)
        {
            Debug.LogError("[HasteTogether] NetworkManager not initialized.");
            return;
        }
        Plugin.JoinLobbyCommand(LobbyID);
    }
}

public class RefreshPublicLobbyListButton : ButtonSetting, IExposedSetting // No [HasteSetting]
{
    public override void ApplyValue() { }

    public override string GetButtonText() => "Refresh Lobby List";

    public string GetCategory() => "Multiplayer";

    public LocalizedString GetDisplayName() => new UnlocalizedString("Refresh");

    public override void OnClicked(ISettingHandler settingHandler)
    {
        if (Plugin.networkManager != null)
            Plugin.networkManager.RequestPublicLobbyList();
        else
            Debug.LogError("[HasteTogether] NetworkManager not initialized.");
    }
}

// --- SendType Enum ---
public enum SendType
{
    Unreliable,
    Reliable,
}
