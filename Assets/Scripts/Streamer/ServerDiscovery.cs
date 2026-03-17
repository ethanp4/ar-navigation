using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace ARDepthRefinement
{
	/// <summary>
	/// Listens on UDP port 47777 for the server's broadcast beacon.
	/// When found, automatically sets FrameStreamer.serverUrl and triggers connection.
	///
	/// Add this component to the same GameObject as FrameStreamer.
	/// FrameStreamer will NOT attempt to connect until this component
	/// has resolved the server IP.
	///
	/// Status is exposed via the DiscoveryStatus enum so your UI can show
	/// "Searching for server..." before the connection is established.
	///
	/// Android: Requires the following permissions in AndroidManifest.xml:
	///   <uses-permission android:name="android.permission.INTERNET" />
	///   <uses-permission android:name="android.permission.ACCESS_WIFI_STATE" />
	///   <uses-permission android:name="android.permission.CHANGE_WIFI_STATE" />
	///   <uses-permission android:name="android.permission.CHANGE_WIFI_MULTICAST_STATE" />
	/// </summary>
	public class ServerDiscovery : MonoBehaviour
	{
		public enum DiscoveryStatus
		{
			Searching,
			Found,
			Failed
		}

		[Header("Discovery Settings")]
		[SerializeField] int discoveryPort = 47777;
		[SerializeField] float timeoutSeconds = 30f;
		[Tooltip("Magic prefix the server beacon must start with.")]
		[SerializeField] string beaconPrefix = "AR_DEPTH_SERVER";
		[SerializeField] int wsPort = 8765;

		[Header("References")]
		[SerializeField] FrameStreamer frameStreamer;

		// ── Public state ──────────────────────────────────────────────────────
		public DiscoveryStatus Status { get; private set; } = DiscoveryStatus.Searching;
		public string ResolvedIP { get; private set; } = string.Empty;

		// ── Internal ──────────────────────────────────────────────────────────
		Socket _socket;
		Thread _listenThread;
		bool _found = false;
		bool _running = false;

		// Thread-safe pending IP to hand off to main thread
		string _pendingIP = null;
		readonly object _ipLock = new object();

		float _searchTimer = 0f;

#if UNITY_ANDROID && !UNITY_EDITOR
		AndroidJavaObject _multicastLock;
#endif

		// ──────────────────────────────────────────────────────────────────────
		void Start()
		{
      #if UNITY_ANDROID && !UNITY_EDITOR
      // Request permissions at runtime — no manifest entries needed for these
      if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission("android.permission.CHANGE_WIFI_MULTICAST_STATE"))
          UnityEngine.Android.Permission.RequestUserPermission("android.permission.CHANGE_WIFI_MULTICAST_STATE");
      if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission("android.permission.ACCESS_WIFI_STATE"))
          UnityEngine.Android.Permission.RequestUserPermission("android.permission.ACCESS_WIFI_STATE");
      #endif
      
			if (frameStreamer == null)
				frameStreamer = GetComponent<FrameStreamer>();

			if (frameStreamer == null)
			{
				Debug.LogError("[ServerDiscovery] No FrameStreamer found. Attach both to the same GameObject.");
				Status = DiscoveryStatus.Failed;
				return;
			}

			// Block FrameStreamer from connecting until we have an IP
			frameStreamer.discoveryReady = false;

			AcquireMulticastLock();
			StartListening();
		}

		// ── Android multicast lock ────────────────────────────────────────────
		void AcquireMulticastLock()
		{
#if UNITY_ANDROID && !UNITY_EDITOR
			try
			{
				using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
				using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
				using var wifiManager = activity.Call<AndroidJavaObject>("getSystemService", "wifi");

				_multicastLock = wifiManager.Call<AndroidJavaObject>("createMulticastLock", "ARDepthDiscovery");
				_multicastLock.Call("acquire");
				Debug.Log("[ServerDiscovery] MulticastLock acquired.");
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[ServerDiscovery] Failed to acquire MulticastLock: {e.Message}");
			}
#endif
		}

		void ReleaseMulticastLock()
		{
#if UNITY_ANDROID && !UNITY_EDITOR
			try
			{
				if (_multicastLock != null)
				{
					bool held = _multicastLock.Call<bool>("isHeld");
					if (held) _multicastLock.Call("release");
					_multicastLock.Dispose();
					_multicastLock = null;
					Debug.Log("[ServerDiscovery] MulticastLock released.");
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[ServerDiscovery] Failed to release MulticastLock: {e.Message}");
			}
#endif
		}

		// ── Socket setup ──────────────────────────────────────────────────────
		void StartListening()
		{
			_running = true;
			_found = false;
			Status = DiscoveryStatus.Searching;

			try
			{
				var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
				sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
				sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
				sock.ReceiveTimeout = 2000; // 2 s so the loop can check _running / _found
				sock.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
				_socket = sock;
			}
			catch (Exception e)
			{
				Debug.LogError($"[ServerDiscovery] Could not bind UDP port {discoveryPort}: {e.Message}");
				Status = DiscoveryStatus.Failed;
				return;
			}

			_listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "ServerDiscovery" };
			_listenThread.Start();

			Debug.Log($"[ServerDiscovery] Listening for server beacon on UDP port {discoveryPort}...");
		}

		// ── Background thread ─────────────────────────────────────────────────
		void ListenLoop()
		{
			byte[] buf = new byte[256];
			EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

			while (_running && !_found)
			{
				try
				{
					int len = _socket.ReceiveFrom(buf, ref remoteEP);
					string msg = Encoding.UTF8.GetString(buf, 0, len);

					Debug.Log($"[ServerDiscovery] Raw packet: '{msg}' from {remoteEP}");

					if (!msg.StartsWith(beaconPrefix))
						continue;

					// Beacon format: "AR_DEPTH_SERVER:<ip>"
					// The IP after the colon is the authoritative address the server knows about itself.
					// Fall back to remoteEP.Address if the beacon has no colon.
					string ip = ((IPEndPoint)remoteEP).Address.ToString();

					int colonIdx = msg.IndexOf(':');
					if (colonIdx >= 0 && colonIdx < msg.Length - 1)
						ip = msg.Substring(colonIdx + 1).Trim();

					lock (_ipLock)
					{
						_pendingIP = ip;
					}

					_found = true;
				}
				catch (SocketException se) when (se.SocketErrorCode == SocketError.TimedOut)
				{
					// Expected — loop again so we can check _running / _found
				}
				catch (SocketException)
				{
					// Socket was closed from main thread — normal shutdown
					break;
				}
				catch (Exception e)
				{
					Debug.LogWarning($"[ServerDiscovery] UDP receive error: {e.Message}");
				}
			}
		}

		// ── Main thread polling ───────────────────────────────────────────────
		void Update()
		{
			if (Status == DiscoveryStatus.Found || Status == DiscoveryStatus.Failed)
				return;

			// Check for resolved IP from background thread
			string ip = null;
			lock (_ipLock)
			{
				if (_pendingIP != null)
				{
					ip = _pendingIP;
					_pendingIP = null;
				}
			}

			if (ip != null)
			{
				OnServerFound(ip);
				return;
			}

			// Timeout check
			_searchTimer += Time.deltaTime;
			if (_searchTimer >= timeoutSeconds)
			{
				Debug.LogWarning($"[ServerDiscovery] No server found after {timeoutSeconds}s. " +
				                 "Make sure server_discovery.py is running on the server.");
				Status = DiscoveryStatus.Failed;
				StopListening();
			}
		}

		void OnServerFound(string ip)
		{
			ResolvedIP = ip;
			Status = DiscoveryStatus.Found;

			string wsUrl = $"ws://{ip}:{wsPort}";
			frameStreamer.serverUrl = wsUrl;
			frameStreamer.discoveryReady = true;

			Debug.Log($"[ServerDiscovery] Server found at {ip} — connecting on {wsUrl}");
			StopListening();
		}

		// ──────────────────────────────────────────────────────────────────────
		void StopListening()
		{
			_running = false;
			try { _socket?.Close(); } catch (Exception) { }
			_socket = null;
			ReleaseMulticastLock();
		}

		void OnDestroy()
		{
			StopListening();
			if (_listenThread != null && _listenThread.IsAlive)
				_listenThread.Join(200);
		}
	}
}