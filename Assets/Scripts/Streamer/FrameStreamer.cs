using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using NativeWebSocket;   // install via: https://github.com/endel/NativeWebSocket

namespace ARDepthRefinement
{
	/// <summary>
	/// Captures the AR camera image every N frames, compresses to JPEG,
	/// and sends it to the depth server over WebSocket.
	/// Waits for ServerDiscovery to resolve the server IP before connecting.
	/// </summary>
	public class FrameStreamer : MonoBehaviour
	{
		[Header("AR Camera")]
		[SerializeField] ARCameraManager cameraManager;

		[Header("Server")]
		[Tooltip("Set automatically by ServerDiscovery.")]
		[HideInInspector] public string serverUrl = "";

		[Header("Streaming Quality")]
		[Range(1, 6)]
		[Tooltip("Send every Nth frame. 3 = ~10fps at 30fps game, keeps bandwidth manageable.")]
		[SerializeField] int frameInterval = 3;
		[Range(10, 90)]
		[Tooltip("JPEG quality. 50-60 is the sweet spot for this use case.")]
		[SerializeField] int jpegQuality = 55;
		[SerializeField] int captureWidth = 320;
		[SerializeField] int captureHeight = 240;

		[Header("Reconnection")]
		[SerializeField] float reconnectDelaySec = 3f;

		[Header("Server Depth Size")]
		[Tooltip("Must match INFER_SIZE in receive_image.py — default is 518.")]
		[SerializeField] int serverDepthSize = 518;

		// ── State ─────────────────────────────────────────────────────────────
		WebSocket _ws;
		Texture2D _captureTex;
		int _frameCount = 0;
		bool _connecting = false;

		// Per-send timestamp keyed by send index for accurate RTT
		int _sendIndex = 0;
		float _lastSendTime = 0f;

		/// <summary>
		/// Set to true by ServerDiscovery once the server IP is resolved.
		/// FrameStreamer will not attempt to connect until this is true.
		/// </summary>
		[HideInInspector] public bool discoveryReady = false;

		[HideInInspector]
		public bool IsConnected =>
			_ws != null && _ws.State == WebSocketState.Open;

		// ──────────────────────────────────────────────────────────────────────
		void Start()
		{
			_captureTex = new Texture2D(captureWidth, captureHeight, TextureFormat.RGB24, false);
			StartCoroutine(ConnectLoop());
		}

		IEnumerator ConnectLoop()
		{
			// Wait until ServerDiscovery has resolved an IP
			yield return new WaitUntil(() => discoveryReady);

			if (string.IsNullOrEmpty(serverUrl))
			{
				Debug.LogError("[FrameStreamer] discoveryReady is true but serverUrl is empty. Cannot connect.");
				yield break;
			}

			while (true)
			{
				if (!IsConnected && !_connecting)
					yield return StartCoroutine(Connect());
				yield return new WaitForSeconds(reconnectDelaySec);
			}
		}

		IEnumerator Connect()
		{
			_connecting = true;
			Debug.Log($"[FrameStreamer] Connecting to {serverUrl}...");

			_ws = new WebSocket(serverUrl);

			_ws.OnOpen    += () => Debug.Log("[FrameStreamer] Connected to depth server.");
			_ws.OnClose   += (code) => Debug.LogWarning($"[FrameStreamer] Disconnected: {code}");
			_ws.OnError   += (err) => Debug.LogError($"[FrameStreamer] WS error: {err}");
			_ws.OnMessage += OnMessageReceived;

			var connectTask = _ws.Connect();
			yield return new WaitUntil(() => connectTask.IsCompleted);
			_connecting = false;
		}

		// ── Per-frame capture & send ──────────────────────────────────────────
		void Update()
		{
			if (_ws != null) _ws.DispatchMessageQueue();
			if (!IsConnected) return;

			if (++_frameCount % frameInterval != 0) return;

			if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImg)) return;

			using (cpuImg)
			{
				// inputRect must cover the FULL source image, not the output size
				var convParams = new XRCpuImage.ConversionParams
				{
					inputRect        = new RectInt(0, 0, cpuImg.width, cpuImg.height),
					outputDimensions = new Vector2Int(captureWidth, captureHeight),
					outputFormat     = TextureFormat.RGB24,
					transformation   = XRCpuImage.Transformation.MirrorY
				};

				cpuImg.Convert(convParams, _captureTex.GetRawTextureData<byte>());
				_captureTex.Apply();
			}

			byte[] jpg = _captureTex.EncodeToJPG(jpegQuality);

			_lastSendTime = Time.realtimeSinceStartup;
			_ = _ws.Send(jpg);
		}

		// ── Receive depth map from server ─────────────────────────────────────
		void OnMessageReceived(byte[] data)
		{
			// receive_image.py sends raw float32 depth bytes only — no timestamp header.
			// Server output is always serverDepthSize x serverDepthSize (518x518 by default).
			int expectedBytes = serverDepthSize * serverDepthSize * 4;
			if (data.Length < expectedBytes)
			{
				Debug.LogWarning($"[FrameStreamer] Undersized depth reply: got {data.Length} bytes, " +
				                 $"expected {expectedBytes} ({serverDepthSize}x{serverDepthSize} float32). Skipping.");
				return;
			}

			float rttMs = (Time.realtimeSinceStartup - _lastSendTime) * 1000f;

			if (DepthInjector.Instance != null)
				DepthInjector.Instance.EnqueueServerDepth(data, serverDepthSize, serverDepthSize, rttMs);
		}

		// ──────────────────────────────────────────────────────────────────────
		async void OnApplicationQuit()
		{
			if (_ws != null) await _ws.Close();
		}

		void OnDestroy()
		{
			if (_captureTex != null) Destroy(_captureTex);
		}
	}
}