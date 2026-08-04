/* ==========================================================================
   AetherCare — Central Config
   ==========================================================================
   Everything you'd otherwise hunt for across files lives here. Load this
   BEFORE app.js (index.html already does this). Nothing below requires
   touching app.js, signaling_server.py, or UnityVRWebRTC.cs — those all
   read their equivalent of this same config.

   Quick picks for your scenarios:

   ┌─────────────────────────────────────────┬──────────────┬─────────────┐
   │ Scenario                                 │ SIGNALING    │ TURN        │
   ├─────────────────────────────────────────┼──────────────┼─────────────┤
   │ Same wifi, no internet needed             │ 'local'      │ 'none'      │
   │ Doctor on 4G/5G, patient on clinic wifi   │ 'firebase'   │ 'cloudflare'│
   │ GitHub Pages, any network                 │ 'firebase'   │ 'cloudflare'│
   │ Classroom on LAN (teacher + students)     │ 'local'      │ 'none'      │
   │ Classroom, students scattered off-LAN     │ 'firebase'   │ 'cloudflare'│
   └─────────────────────────────────────────┴──────────────┴─────────────┘

   Why 'local' signaling pairs with TURN 'none': if you're using the LAN
   server, everyone's already on the same network, so direct P2P (via STUN,
   which is free/unlimited) always succeeds — TURN is never invoked. Only
   turn it on when peers might be on genuinely different networks (mobile
   data, different wifi, different buildings). See docs/ARCHITECTURE_AND_SETUP.md §1.
   ========================================================================== */

window.AETHERCARE_CONFIG = {

  // -------------------------------------------------------------------
  // SIGNALING — how peers find each other and swap the handshake.
  // -------------------------------------------------------------------
  // 'firebase' : cloud, works from any network (mobile data, different
  //              wifi, GitHub Pages). This is what the doctor's roaming
  //              phone needs. Free tier is plenty for this — signaling
  //              messages are tiny JSON blobs, not media.
  // 'local'    : the included Python server, LAN-only, zero internet
  //              dependency. Use on clinic wifi when everyone (doctor,
  //              patient device, VR headset) is on the same network.
  //
  // This is just the *default* shown in the Settings panel — the person
  // can still switch it per-session in the UI. Set it to whichever mode
  // you use most, so nobody has to think about it day-to-day.
  defaultSignaling: 'firebase',
  firebaseDatabaseUrl: 'https://walkietalkie-c0f03-default-rtdb.asia-southeast1.firebasedatabase.app',
  localServerUrl: '',        // e.g. 'ws://192.168.1.42:8765' — printed by signaling_server.py

  // -------------------------------------------------------------------
  // TURN — what actually gets media through across networks / CGNAT.
  // -------------------------------------------------------------------
  // 'cloudflare' : short-lived credentials fetched at join-time from your
  //                own Cloudflare Worker (see cloudflare-worker/). This is
  //                what you want for the doctor-on-mobile-data pattern —
  //                1,000 GB/month free, then $0.05/GB. See
  //                cloudflare-worker/README.md for the exact setup steps.
  // 'openrelay'  : the free public OpenRelay TURN set. Fine for a quick
  //                test on day one before you've set up the Worker, NOT
  //                fine for sustained real use (shared, rate-limited,
  //                50GB/mo-class free tiers elsewhere run out in days at
  //                8hrs/day usage). Automatic fallback if 'cloudflare' is
  //                selected but the Worker call fails, so you're never
  //                stuck with zero TURN.
  // 'none'       : STUN only, no relay. Only correct when you know every
  //                peer is on the same LAN (i.e. you're using 'local'
  //                signaling) — cheaper and simpler, nothing to configure.
  turnMode: 'openrelay', // <-- change to 'cloudflare' once your Worker is deployed

  // Your deployed Cloudflare Worker URL (see cloudflare-worker/README.md).
  // Only used when turnMode === 'cloudflare'.
  turnCredentialEndpoint: '', // e.g. 'https://aethercare-turn.yoursubdomain.workers.dev/ice-servers'

  // -------------------------------------------------------------------
  // ROOM TOPOLOGY — mesh (default) vs hub-and-spoke (classroom/1-to-many).
  // -------------------------------------------------------------------
  // 'mesh'      : every peer connects to every other peer directly. Right
  //               for 1-to-1 (doctor+patient) and small VR groups. Caps
  //               out around 4-6 peers — beyond that, bandwidth on each
  //               participant's upload multiplies per additional peer.
  // 'hub-spoke' : one 'hub' peer (instructor) connects to every 'spoke'
  //               peer (student); spokes only connect to the hub, never
  //               to each other. This is the right shape for a classroom
  //               or Unity simulation where students stream mic+camera
  //               to one instructor — it scales to far more participants
  //               than mesh because each spoke only opens ONE connection,
  //               not N-1.
  //
  // Each person picks their own role at join time in the Settings panel
  // (Room type: Mesh / Classroom, then Hub or Spoke) — this default just
  // pre-selects it so a recurring classroom setup doesn't need re-picking.
  defaultTopology: 'mesh',  // 'mesh' | 'hub-spoke'
  defaultRole: 'peer',      // 'peer' (mesh) | 'hub' | 'spoke' (hub-spoke)

  // -------------------------------------------------------------------
  // DOMAIN & ROLE LABELS — customize for Healthcare, Defense, Education, Unity Sim
  // -------------------------------------------------------------------
  // Change these to fit your project domain without touching HTML/JS logic:
  // e.g. Healthcare: hostTitle: 'Doctor', memberTitle: 'Patient'
  // e.g. Education:  hostTitle: 'Instructor', memberTitle: 'Student'
  // e.g. Defense:    hostTitle: 'Commander', memberTitle: 'Operator'
  // e.g. Unity Sim:  hostTitle: 'Sim Host', memberTitle: 'VR Client'
  appTitle: 'AetherCare',
  appSubtitle: 'Unified WebRTC Platform',
  hostTitle: 'Host (Doctor / Instructor / Host)',
  memberTitle: 'Member (Patient / Student / Client)',
};
