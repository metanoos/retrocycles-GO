using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using LightRunners.Core;

namespace LightRunners.Gameplay
{
    /// <summary>
    /// Friend-match panel (spec §8.5). Two entry buttons in Lobby — Create Room (mints a
    /// code, shows it large + copy) and Join Room (code field) — then the PartyLobby roster
    /// with a host-only Start Race and a Leave button.
    ///
    /// Start signaling: joiners are NOT in the Photon room while waiting, so everyone in
    /// PartyLobby polls get_lobby every lobbyPollIntervalSeconds; when status flips to
    /// 'racing', joiners auto-run GameManager.StartRun against the same room name. A closed
    /// or vanished lobby drops the player back to Lobby with the reason shown.
    ///
    /// The lobby stays active after a race ends, so Run Again rejoins the same party room;
    /// Leave is the explicit exit.
    /// </summary>
    public class LobbyUIController : MonoBehaviour
    {
        [Header("Lobby-state entry")]
        [SerializeField] private GameObject friendMatchButton;   // opens the panel (Lobby only)
        [SerializeField] private GameObject entryPanel;          // Create / Join
        [SerializeField] private Button createButton;
        [SerializeField] private Button joinButton;
        [SerializeField] private InputField codeInput;
        [SerializeField] private TMP_TextAdaptor errorText;

        [Header("PartyLobby roster")]
        [SerializeField] private GameObject partyPanel;
        [SerializeField] private TMP_TextAdaptor codeText;
        [SerializeField] private TMP_TextAdaptor rosterText;
        [SerializeField] private TMP_TextAdaptor hintText;
        [SerializeField] private Button copyButton;
        [SerializeField] private Button startRaceButton;         // host only
        [SerializeField] private Button leaveButton;

        private ILobbyService Lobby => ServiceLocator.Get<ILobbyService>();
        private Coroutine _poll;

        private void OnEnable()
        {
            if (GameManager.HasInstance)
                GameManager.Instance.OnStateChanged += HandleStateChanged;
        }

        private void OnDisable()
        {
            if (GameManager.HasInstance)
                GameManager.Instance.OnStateChanged -= HandleStateChanged;
        }

        private void Start()
        {
            if (friendMatchButton != null)
            {
                var b = friendMatchButton.GetComponentInChildren<Button>();
                if (b != null) b.onClick.AddListener(ToggleEntryPanel);
            }
            if (createButton != null) createButton.onClick.AddListener(OnCreate);
            if (joinButton != null) joinButton.onClick.AddListener(OnJoin);
            if (copyButton != null) copyButton.onClick.AddListener(OnCopyCode);
            if (startRaceButton != null) startRaceButton.onClick.AddListener(OnStartRace);
            if (leaveButton != null) leaveButton.onClick.AddListener(OnLeave);

            if (GameManager.HasInstance)
                HandleStateChanged(GameState.Initializing, GameManager.Instance.State);
        }

        private void HandleStateChanged(GameState prev, GameState next)
        {
            bool lobby = next == GameState.Lobby;
            bool party = next == GameState.PartyLobby;

            if (friendMatchButton != null) friendMatchButton.SetActive(lobby);
            if (entryPanel != null && !lobby) entryPanel.SetActive(false);
            if (partyPanel != null) partyPanel.SetActive(party);

            if (party) StartPolling();
            else StopPolling();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Entry: create / join
        // ─────────────────────────────────────────────────────────────────────
        private void ToggleEntryPanel()
        {
            if (entryPanel != null) entryPanel.SetActive(!entryPanel.activeSelf);
            errorText?.SetText("");
        }

        private void OnCreate()
        {
            var svc = Lobby;
            if (svc == null) { ShowError("offline"); return; }
            SetEntryInteractable(false);
            svc.CreateLobby(
                onSuccess: info =>
                {
                    SetEntryInteractable(true);
                    EnterParty(info);
                },
                onError: err => { SetEntryInteractable(true); ShowError(err); });
        }

        private void OnJoin()
        {
            var svc = Lobby;
            if (svc == null) { ShowError("offline"); return; }
            string code = codeInput != null ? codeInput.text : "";
            if (string.IsNullOrWhiteSpace(code)) { ShowError("enter a code"); return; }

            SetEntryInteractable(false);
            svc.JoinLobby(code,
                onSuccess: info =>
                {
                    SetEntryInteractable(true);
                    EnterParty(info);
                },
                onError: err => { SetEntryInteractable(true); ShowError(err); });
        }

        private void EnterParty(LobbyInfo info)
        {
            errorText?.SetText("");
            if (entryPanel != null) entryPanel.SetActive(false);
            RefreshParty(info);
            GameManager.Instance?.SetState(GameState.PartyLobby);
        }

        private void SetEntryInteractable(bool on)
        {
            if (createButton != null) createButton.interactable = on;
            if (joinButton != null) joinButton.interactable = on;
        }

        private void ShowError(string token)
        {
            string msg = token switch
            {
                "offline" => "Friend match needs a connection",
                "lobby_full" => "That room is full",
                "lobby_expired" => "That code has expired",
                "lobby_closed" => "That room is closed",
                "rate_limited" => "Too many tries — wait a minute",
                "not_found" => "No room with that code",
                "enter a code" => "Enter a room code first",
                _ => $"Couldn't reach the lobby ({token})",
            };
            errorText?.SetText(msg);
        }

        // ─────────────────────────────────────────────────────────────────────
        // PartyLobby: roster + start signal
        // ─────────────────────────────────────────────────────────────────────
        private void RefreshParty(LobbyInfo info)
        {
            if (info == null) return;
            codeText?.SetText(info.code);

            var sb = new StringBuilder();
            if (info.members != null)
            {
                foreach (var m in info.members)
                {
                    sb.Append(m.displayName ?? "Runner");
                    if (m.userId == info.hostId) sb.Append("  (host)");
                    sb.Append('\n');
                }
            }
            rosterText?.SetText(sb.ToString().TrimEnd());

            bool isHost = Lobby != null && Lobby.IsHost;
            if (startRaceButton != null) startRaceButton.gameObject.SetActive(isHost);
            hintText?.SetText(isHost
                ? "Share the code, then Start Race"
                : "Waiting for the host to start…");
        }

        private void StartPolling()
        {
            StopPolling();
            _poll = StartCoroutine(CoPoll());
        }

        private void StopPolling()
        {
            if (_poll != null) { StopCoroutine(_poll); _poll = null; }
        }

        private IEnumerator CoPoll()
        {
            var wait = new WaitForSecondsRealtime(GameConfig.Active.lobbyPollIntervalSeconds);
            while (true)
            {
                var svc = Lobby;
                var active = svc?.ActiveLobby;
                if (svc == null || active == null) { BackToLobby("lobby_closed"); yield break; }

                bool done = false;
                LobbyInfo latest = null;
                string error = null;
                svc.GetLobby(active.code,
                    onSuccess: info => { latest = info; done = true; },
                    onError: err => { error = err; done = true; });

                while (!done) yield return null;

                if (error != null)
                {
                    if (error == "not_found" || error == "lobby_closed" || error == "lobby_expired")
                    { BackToLobby(error); yield break; }
                    // Transient network error: keep polling.
                }
                else if (latest != null)
                {
                    RefreshParty(latest);
                    if (latest.IsRacing)
                    {
                        // The §8.5 start signal — joiners auto-start into the same room.
                        StopPolling();
                        GameManager.Instance?.StartRun();
                        yield break;
                    }
                    if (latest.status == "closed") { BackToLobby("lobby_closed"); yield break; }
                }

                yield return wait;
            }
        }

        private void BackToLobby(string reason)
        {
            StopPolling();
            Lobby?.LeaveLobby(null);
            ShowError(reason);
            if (entryPanel != null) entryPanel.SetActive(true);
            GameManager.Instance?.SetState(GameState.Lobby);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Buttons
        // ─────────────────────────────────────────────────────────────────────
        private void OnCopyCode()
        {
            var active = Lobby?.ActiveLobby;
            if (active != null) GUIUtility.systemCopyBuffer = active.code;
        }

        private void OnStartRace()
        {
            var svc = Lobby;
            if (svc == null) return;
            if (startRaceButton != null) startRaceButton.interactable = false;
            svc.StartLobbyRace(
                onSuccess: () =>
                {
                    if (startRaceButton != null) startRaceButton.interactable = true;
                    GameManager.Instance?.StartRun();
                },
                onError: err =>
                {
                    if (startRaceButton != null) startRaceButton.interactable = true;
                    hintText?.SetText($"Couldn't start ({err})");
                });
        }

        private void OnLeave()
        {
            StopPolling();
            Lobby?.LeaveLobby(null);
            GameManager.Instance?.SetState(GameState.Lobby);
        }
    }
}
