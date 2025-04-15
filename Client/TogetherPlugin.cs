using System.Text;
using Landfall.Haste;
using Landfall.Modding;
using SettingsLib.Settings;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
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
            LeaveLobby();
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

            // Could add more data here, like player count:
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
    if (packetData == null || packetData.Length == 0) return; // Empty check

    NetworkedPlayer? plr = null;
    byte packetId = packetData[0];
    byte[] payload = packetData.Skip(1).ToArray(); // Get payload (data after ID)

    try
    {
        plr = Plugin.GetOrSetupPlayer(sourceSteamId); // Get player early if possible
        if (plr == null && packetId != 0x02) // Allow name packet to create player
        {
             Debug.LogWarning($"[HasteTogetherP2P] Received packet {packetId} for unknown or invalid player {sourceSteamId}.");
             return;
        }
        if (plr != null && plr.animator == null && packetId >= 0x10) // Check animator for animation packets
        {
             Debug.LogWarning($"[HasteTogetherP2P] Received animation packet {packetId} for player {sourceSteamId} but animator is missing.");
             return;
        }


        switch (packetId)
        {
            case 0x01: // Player Transform Update
                if (payload.Length < 16) { Debug.LogWarning($"[HasteTogetherP2P] Malformed Transform packet (ID 0x01) from {sourceSteamId}. Length: {payload.Length}"); break; }
                // Deserialize directly here or pass to ApplyTransform
                UpdatePacket.Deserialize(payload, out Vector3 pos, out Quaternion rot, out bool grounded);
                plr?.ApplyTransformAndState(pos, rot, grounded); // New method in NetworkedPlayer
                break;

            case 0x02: // Player Name Update
                if (payload.Length < 1) { Debug.LogWarning($"[HasteTogetherP2P] Malformed Name packet (ID 0x02) from {sourceSteamId}. Length: {payload.Length}"); break; }
                // Ensure player exists *after* getting name
                 plr = Plugin.GetOrSetupPlayer(sourceSteamId);
                 if (plr == null) break;
                string receivedName = Encoding.UTF8.GetString(payload);
                plr.playerName = receivedName;
                Debug.Log($"[HasteTogetherP2P] Received name for {sourceSteamId}: {plr.playerName}");
                break;

            // --- Animation Event Cases ---
            case 0x10: // Jump
                plr?.animator?.SetTrigger("JumpTrigger"); // Assuming a trigger named "JumpTrigger" exists
                // Or call a method if NetworkedPlayer has one: plr.HandleRemoteJump();
                break;
            case 0x11: // Land
                var landPacket = LandPacket.Deserialize(payload);
                // We need to replicate the logic from PlayerAnimationHandler.Land based on type
                // This might involve playing specific state names directly
                string landAnimState = "New_Courier_Dash_LandType1"; // Default/Bad/Ok
                if (landPacket.LandingType == LandingType.Good) landAnimState = "New_Courier_Dash_LandType3";
                else if (landPacket.LandingType == LandingType.Perfect) landAnimState = "New_Courier_Dash_LandType4";
                plr?.animator?.Play(landAnimState, 0, 0f);
                // Maybe set a landing type parameter if the animator uses one:
                // plr.animator.SetInteger("LandingType", (int)landPacket.LandingType);
                break;
            case 0x12: // WallBounce
                plr?.animator?.SetBool("Wall Bounce", true); // Set bool, needs to be reset in NetworkedPlayer.Update
                break;
            case 0x13: // Wave
                plr?.animator?.SetBool("Wave", true); // Set bool, needs to be reset in NetworkedPlayer.Update
                break;
            case 0x14: // TakeDamage
                var damagePacket = TakeDamagePacket.Deserialize(payload);
                plr?.animator?.SetFloat("DamageDir X", damagePacket.DamageDirectionValue);
                plr?.animator?.SetBool("Damaged", true); // Set bool, needs to be reset in NetworkedPlayer.Update
                break;
            case 0x15: // SetShardAnim
                var shardPacket = SetShardAnimPacket.Deserialize(payload);
                plr?.animator?.SetInteger("Shard Exit Type", shardPacket.AnimationId);
                break;
            case 0x16: // SetConfidence
                var confidencePacket = SetConfidencePacket.Deserialize(payload);
                plr?.animator?.SetFloat("Confidence", confidencePacket.ConfidenceValue);
                break;
            case 0x17: // PlayAnimation
                var playPacket = PlayAnimationPacket.Deserialize(payload);
                plr?.animator?.Play(playPacket.AnimationName, 0, 0f);
                break;
             case 0x18: // GrappleState
                var grapplePacket = GrappleStatePacket.Deserialize(payload);
                plr?.ApplyGrappleState(grapplePacket.IsGrappling, grapplePacket.GrappleVector); // New method in NetworkedPlayer
                break;

            default:
                Debug.LogWarning($"[HasteTogetherP2P] Received unknown packet ID: {packetId} from {sourceSteamId}");
                break;
        }
    }
    catch (Exception ex)
    {
        Debug.LogError($"[HasteTogetherP2P] Error processing packet ID {packetId} from {sourceSteamId}: {ex}");
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
    private bool targetIsGrounded = false; // Store grounded state

    private float interpolationSpeed = 15.0f;
    private float positionThresholdSqr = 0.0001f;
    private float rotationThreshold = 0.5f;

    // For calculating velocity
    private Vector3 previousPosition;
    private float lastUpdateTime;

    // For resetting bools
    private float wallBounceResetTimer = 0f;
    private float waveResetTimer = 0f;
    private float damagedResetTimer = 0f;

    // Player Name UI
    private string _playerName = "Loading...";
    public string playerName { /* ... getter/setter remains the same ... */
        get => _playerName;
            set
            {
                _playerName = value ?? "InvalidName";
                gameObject.name = $"HasteTogetherP2P_{_playerName}_{steamId}";
                if (playerNameText != null)
                {
                    playerNameText.text = _playerName;
                }
                else
                {
                    SetupPlayerUI(); // Attempt setup if UI wasn't ready
                    if (playerNameText != null) playerNameText.text = _playerName;
                }
            }
     }
    public Canvas playerCanvas = null!;
    public TextMeshProUGUI playerNameText = null!;

    void Awake()
    {
        // Initialize target transform to current transform
        targetPosition = transform.position;
        targetRotation = transform.rotation;
        previousPosition = transform.position; // Init for velocity calc
        lastUpdateTime = Time.time;
        SetupPlayerUI();
        if (_playerName == "Loading...") playerName = SteamFriends.GetFriendPersonaName(steamId);
    }

    // Renamed ApplyTransform to ApplyTransformAndState
    public void ApplyTransformAndState(Vector3 position, Quaternion rotation, bool isGrounded)
    {
        targetPosition = position;
        targetRotation = rotation;
        targetIsGrounded = isGrounded; // Store the received grounded state
    }

     // New method to handle grapple state updates
    public void ApplyGrappleState(bool isGrappling, Vector3 grappleVector)
    {
        if (animator != null)
        {
            animator.SetBool("Grapple", isGrappling);
            if (isGrappling)
            {
                // Set vector components if grappling
                animator.SetFloat("Grapple X", grappleVector.x);
                animator.SetFloat("Grapple Y", grappleVector.y);
                animator.SetFloat("Grapple Z", grappleVector.z);
            }
        }
    }


    private void Update()
    {
        // --- Interpolation ---
        float lerpFactor = Time.deltaTime * interpolationSpeed;
        transform.position = Vector3.Lerp(transform.position, targetPosition, lerpFactor);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, lerpFactor);

        // --- Animator Updates ---
        if (animator != null)
        {
            // Calculate velocity based on interpolated movement
            float timeSinceUpdate = Time.time - lastUpdateTime;
            Vector3 currentVelocity = Vector3.zero;
            if (timeSinceUpdate > 0.01f) // Avoid division by zero and use threshold
            {
                 // Use target position for velocity calc to reflect incoming data more closely
                currentVelocity = (targetPosition - previousPosition) / timeSinceUpdate;
                previousPosition = targetPosition; // Update previous position based on target
                lastUpdateTime = Time.time;
            }
             else {
                 // If no recent update, estimate velocity based on current movement
                 currentVelocity = (transform.position - previousPosition) / Time.deltaTime;
                 previousPosition = transform.position; // Update based on current interpolated pos
             }


            // Set basic movement parameters
            animator.SetFloat("Speed", currentVelocity.magnitude / 100f); // Adjust divisor based on game scale
            animator.SetFloat("Velocity Y", currentVelocity.y);
            animator.SetBool("Is Grounded", targetIsGrounded); // Use the synced grounded state

            // TODO: Set Relative Input X/Y based on velocity relative to player forward if needed
            // Vector3 localVel = transform.InverseTransformDirection(currentVelocity);
            // animator.SetFloat("Relative Input X", localVel.x * someFactor);
            // animator.SetFloat("Relative Input Y", localVel.z * someFactor);

            // Reset temporary bools after a short delay
            ResetAnimatorBool("Wall Bounce", ref wallBounceResetTimer);
            ResetAnimatorBool("Wave", ref waveResetTimer);
            ResetAnimatorBool("Damaged", ref damagedResetTimer);
        }

        // --- UI Update ---
        if (playerCanvas != null && Camera.main != null)
        {
            playerCanvas.transform.LookAt(
                playerCanvas.transform.position + Camera.main.transform.rotation * Vector3.forward,
                Camera.main.transform.rotation * Vector3.up
            );
        }
    }

    // Helper to reset animator bools after a delay
    private void ResetAnimatorBool(string boolName, ref float timer)
    {
        if (animator.GetBool(boolName))
        {
            timer += Time.deltaTime;
            if (timer > 0.1f) // Reset after 0.1 seconds
            {
                animator.SetBool(boolName, false);
                timer = 0f;
            }
        } else {
            timer = 0f; // Reset timer if bool is already false
        }
    }


    // SetupPlayerUI and OnDestroy remain the same
    void SetupPlayerUI()
    {
         if (playerCanvas != null || !this.enabled) return; // Already setup or component disabled

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
                    Debug.LogWarning("[HasteTogetherP2P] TMP Default Font Asset is null. Nametag may not render.");

                RectTransform textRect = textGO.GetComponent<RectTransform>();
                textRect.localPosition = Vector3.zero;
                textRect.sizeDelta = canvasRect.sizeDelta;
                textRect.localScale = Vector3.one;

                playerNameText.ForceMeshUpdate(); // Update mesh

                Debug.Log($"[HasteTogetherP2P] Setup UI for {steamId}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[HasteTogetherP2P] Error setting up player UI for {steamId}: {e}");
                if (playerCanvas != null) Destroy(playerCanvas.gameObject);
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


// --- Settings Classes --- ///


// Lobby Creation Settings
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
