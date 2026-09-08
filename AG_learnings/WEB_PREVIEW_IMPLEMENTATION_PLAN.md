# Web Architecture & Integration Plan: SDVoE Encoder Preview for BlueRiver AV Manager

## 1. Context & Objective
The goal is to allow **BlueRiver AV Manager** (Semtech's web-based PWA / Electron AV management GUI) to display **live encoder video previews** directly in the web browser interface.

### The Challenge with Browsers & Multicast:
Web browsers (Chrome, Edge, Firefox) **cannot** join UDP multicast groups or receive raw UDP packets directly due to web sandbox security policies and the lack of raw socket APIs.

---

## 2. Recommended Solution Architecture

`
+----------------------------------------------------------------------------------------------------+
|                                    SDVoE 10G / 1G Network                                          |
|                                                                                                    |
|   +-----------------------+              UDP Multicast (RFC 4175 YUV422)                           |
|   | Semtech / AVAS        | -------------------------------------------------+                     |
|   | SDVoE Encoder (TX)    |  224.1.1.X:10000 (1 - 5 FPS)                     |                     |
|   +-----------------------+                                                  |                     |
+------------------------------------------------------------------------------|---------------------+
                                                                               v
+----------------------------------------------------------------------------------------------------+
| Node.js / Go / Rust Lightweight "Preview Gateway Sidecar" (Runs locally or on Control Server PC)   |
|                                                                                                    |
|  1. dgram.createSocket('udp4') -> joins multicast group 224.1.1.X:10000                           |
|  2. Reassembles RTP packets (LineNo, Marker, SequenceNumber)                                       |
|  3. Decodes YUV422 -> RGB -> Encodes to JPEG / WebP / Canvas buffer                                |
|  4. Serves stream via WebSocket, HTTP MJPEG (/preview/:tx_mac), or Server-Sent Events (SSE)        |
+----------------------------------------------------------------------------------------------------+
                                                                               |
                                                                               | WebSocket / MJPEG / HTTP
                                                                               v
+----------------------------------------------------------------------------------------------------+
| BlueRiver AV Manager (PWA Web App / Electron)                                                      |
|                                                                                                    |
|  +-----------------------------------------------------------------------------------------------+ |
|  | [Web UI Component: Encoder Card / Modal]                                                     | |
|  |                                                                                               | |
|  |   <img src="http://localhost:8080/api/preview/00:11:22:33:44:55/stream.mjpeg" />              | |
|  |   OR                                                                                          | |
|  |   <canvas id="tx-preview-canvas"></canvas>  <-- WebSocket binary frames                        | |
|  |                                                                                               | |
|  |   - Auto-refreshes at 2-5 FPS                                                                 | |
|  |   - Zero browser plugins required                                                             | |
|  +-----------------------------------------------------------------------------------------------+ |
+----------------------------------------------------------------------------------------------------+
`

---

## 3. Sidecar Gateway Implementation Options

### Option A: Node.js / TypeScript Gateway (Fastest, Native to Web Stack)
- **Dependencies**: dgram (built-in UDP), ws (WebSockets), sharp or @napi-rs/canvas or jpeg-js (fast native turbojpeg encoder).
- **Mechanism**:
  1. Node.js listens on UDP multicast:
     `js
     const socket = dgram.createSocket({ type: 'udp4', reuseAddr: true });
     socket.bind(multicastPort, () => {
       socket.addMembership(multicastIP, localNIC_IP);
     });
     `
  2. Parses RTP headers (20 bytes) in high-performance Buffer slices.
  3. When Marker == 1, converts YUV422 buffer into a WebP or JPEG frame in memory (~5ms).
  4. Pushes the JPEG buffer over WebSocket as binary blobs or serves an HTTP multipart MJPEG stream.

### Option B: WebAssembly (Wasm) in Browser + WebTransport/WebSocket Raw Relay
If you want zero image compression and maximum quality:
1. Sidecar relays raw UDP datagrams or raw reassembled frames over WebSocket directly to the browser.
2. A WebAssembly module (compiled from C/Rust) executes the YUV422ToRGB conversion in browser memory.
3. Renders directly onto an HTML5 <canvas> via ImageData or WebGL texture.

---

## 4. Integration into BlueRiver AV Manager

BlueRiver AV Manager is built with web technologies (HTML5/CSS/JavaScript PWA and Electron). Integrating the preview overlay requires three elements:

### 4.1 Control Plane Trigger
When the user clicks or hovers over an Encoder in BlueRiver AV Manager to preview:
1. The web app calls the BlueRiver Control Server REST API (port 9200) or Telnet API (port 6970):
   `json
   POST /api/devices/{device_mac}/thumbnail
   {
     "state": "start",
     "multicast_ip": "224.1.1.100",
     "port": 10000
   }
   `
2. The web app notifies the Preview Gateway:
   `http
   POST http://localhost:8080/api/preview/start
   {
     "mac": "{device_mac}",
     "multicast_ip": "224.1.1.100",
     "port": 10000
   }
   `

### 4.2 Web UI Overlay Component
Embed an overlay card or floating thumbnail window onto the device grid:
`html
<div class="encoder-preview-card">
  <div class="header">Live Stream Preview: {{ device.name }}</div>
  <img 
    src="http://localhost:8080/api/preview/{{ device.mac }}/stream.mjpeg" 
    alt="Live Encoder Stream"
    class="preview-image"
    loading="eager"
  />
  <div class="footer">Format: YUV422 | Rate: 2-5 FPS</div>
</div>
`

---

## 5. Summary of Key Parameters for Developers

| Parameter | Value / Recommendation |
| :--- | :--- |
| **Transport** | UDP Multicast |
| **Multicast IP Range** | 224.1.1.1 - 224.1.3.255 (configurable) |
| **Multicast Port** | 10000 (typical default) |
| **Framing Protocol** | RFC 3550 RTP + RFC 4175 (Uncompressed Video Payload) |
| **Pixel Encoding** | YUV 4:2:2 (2 bytes per pixel) |
| **Expected Resolution** | 320x180, 480x270, or 640x360 |
| **Refresh Interval** | 200ms â€“ 1000ms (1 to 5 FPS) |
| **Recommended Web Protocol** | MJPEG (simplest <img src=...> drop-in) or WebSocket binary frames to <canvas> |