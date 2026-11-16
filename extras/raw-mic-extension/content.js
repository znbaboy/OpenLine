// Runs in the page before its own scripts. Forces the browser's voice processing off for mic
// requests on the sites in manifest.json. Your selected mic (e.g. CABLE Output) is untouched.
(() => {
  const OFF = { echoCancellation: false, noiseSuppression: false, autoGainControl: false };
  const LEGACY = ["googEchoCancellation", "googNoiseSuppression", "googAutoGainControl", "googHighpassFilter"];

  const raw = (audio) => {
    const a = audio === true || audio == null ? {} : { ...audio };
    Object.assign(a, OFF);
    for (const k of LEGACY) if (k in a) a[k] = false;
    if (Array.isArray(a.advanced)) {
      a.advanced = a.advanced.map((set) => {
        const s = { ...set };
        for (const k of [...Object.keys(OFF), ...LEGACY]) delete s[k];
        return s;
      });
    }
    return a;
  };

  // Diagnostics state (see openlineCheck below).
  const micTracks = new WeakSet();
  const pcs = new Set();
  const worklets = [];

  // Patch the prototype, not the instance, so saved references and .call() are covered too.
  const mdProto = globalThis.MediaDevices && MediaDevices.prototype;
  if (mdProto && typeof mdProto.getUserMedia === "function") {
    const original = mdProto.getUserMedia;
    mdProto.getUserMedia = async function (constraints) {
      const stream = await original.call(this,
        constraints && constraints.audio ? { ...constraints, audio: raw(constraints.audio) } : constraints);
      for (const t of stream.getAudioTracks()) micTracks.add(t);
      return stream;
    };
  }

  // Sites can re-enable processing later on the live track; keep it off.
  const proto = globalThis.MediaStreamTrack && MediaStreamTrack.prototype;
  if (proto && typeof proto.applyConstraints === "function") {
    const apply = proto.applyConstraints;
    proto.applyConstraints = function (c) {
      return apply.call(this, this.kind === "audio" ? raw(c) : c);
    };
    const clone = proto.clone;
    proto.clone = function () {
      const t = clone.call(this);
      if (micTracks.has(this)) micTracks.add(t);
      return t;
    };
  }

  // Track peer connections and AudioWorklet modules so we can see what is actually sent.
  if (globalThis.RTCPeerConnection) {
    globalThis.RTCPeerConnection = new Proxy(globalThis.RTCPeerConnection, {
      construct(target, args, newTarget) {
        const pc = Reflect.construct(target, args, newTarget);
        pcs.add(pc);
        return pc;
      },
    });
  }
  // Chromium sends Opus at ~32 kbps mono, tuned for speech: music and effects sound thin.
  // Our send bitrate is set by the *remote* SDP's Opus fmtp, so rewrite it (and our own, so the
  // other side's audio improves too), then lift the sender cap.
  const OPUS = { stereo: "1", "sprop-stereo": "1", maxaveragebitrate: "128000", usedtx: "0" };
  const BITRATE = 128000;

  const hifi = (sdp) => {
    if (typeof sdp !== "string") return sdp;
    const pts = [...sdp.matchAll(/a=rtpmap:(\d+) opus\/48000/gi)].map((m) => m[1]);
    for (const pt of pts) {
      const fmtp = new RegExp(`a=fmtp:${pt} ([^\\r\\n]*)`);
      const m = sdp.match(fmtp);
      const params = new Map(
        (m ? m[1] : "").split(";").map((p) => p.trim()).filter(Boolean).map((p) => p.split("=")));
      for (const [k, v] of Object.entries(OPUS)) params.set(k, v);
      const line = `a=fmtp:${pt} ${[...params].map(([k, v]) => `${k}=${v}`).join(";")}`;
      sdp = m ? sdp.replace(fmtp, line)
              : sdp.replace(new RegExp(`(a=rtpmap:${pt} opus/48000[^\\r\\n]*)`, "i"), `$1\r\n${line}`);
    }
    return sdp;
  };

  const liftSenders = (pc) => {
    for (const s of pc.getSenders()) {
      if (!s.track || s.track.kind !== "audio") continue;
      const p = s.getParameters();
      if (!p.encodings || !p.encodings.length) continue;
      p.encodings.forEach((e) => (e.maxBitrate = BITRATE));
      s.setParameters(p).catch(() => {});
    }
  };

  const pcProto = globalThis.RTCPeerConnection && RTCPeerConnection.prototype;
  if (pcProto) {
    for (const name of ["setLocalDescription", "setRemoteDescription"]) {
      const orig = pcProto[name];
      pcProto[name] = function (desc, ...rest) {
        const d = desc && desc.sdp ? { type: desc.type, sdp: hifi(desc.sdp) } : desc;
        const r = orig.call(this, d, ...rest);
        if (r && typeof r.then === "function") r.then(() => liftSenders(this), () => {});
        return r;
      };
    }
  }

  const aw = globalThis.AudioWorklet && AudioWorklet.prototype;
  if (aw && typeof aw.addModule === "function") {
    const add = aw.addModule;
    aw.addModule = function (url, ...rest) {
      worklets.push(String(url));
      return add.call(this, url, ...rest);
    };
  }

  // In DevTools during a chat, run: openlineCheck()
  //   fromMic true  + all three false -> browser processing is off; any filtering is on the site/remote side.
  //   fromMic false -> the site re-routes the mic through Web Audio before sending (its own filter).
  //   no rows       -> wrong frame selected in DevTools, or no call active.
  globalThis.openlineCheck = () => {
    const rows = [...pcs]
      .filter((pc) => pc.connectionState !== "closed")
      .flatMap((pc) => pc.getSenders())
      .filter((s) => s.track && s.track.kind === "audio")
      .map((s) => {
        const g = s.track.getSettings();
        return {
          fromMic: micTracks.has(s.track),
          label: s.track.label,
          enabled: s.track.enabled,
          echoCancellation: g.echoCancellation,
          noiseSuppression: g.noiseSuppression,
          autoGainControl: g.autoGainControl,
          channelCount: g.channelCount,
        };
      });
    console.table(rows);
    console.log("AudioWorklet modules loaded:", worklets.length ? worklets : "none");
    return rows;
  };
})();
