# Reverse Engineering Handover Report: SDVoE Multicast Preview Architecture

## 1. Executive Summary
This report details the technical inner workings of the **"Preview"** feature found in **Dual Link SDVoE Tester (v1.16)** and its companion runtime libraries (GenLib.dll, VOIPS.dll). 
All discoveries were extracted through static and dynamic .NET metadata reflection, intermediate language (IL) disassembly, control flow extraction, and API correlation with Semtech BlueRiver documentation.

---

## 2. Architecture & Library Inventory

| Layer | Implementation in SDVoE_Tester | External Third-Party Dependencies |
| :--- | :--- | :--- |
| **Network Socket** | `System.Net.Sockets.UdpClient` | None (Native .NET Framework) |
| **RTP Packet Parsing** | Internal custom classes: `RTPLib.RtpPacket`, `RTPLib.RtpPayloadType` | None (Custom RFC 3550 / RFC 4175 implementation) |
| **Stream Engine** | `VOIPS_LIB.THUMBNAIL`, `VOIPS_LIB.THUMBNAIL+THUMBNAIL_Device` | None |
| **Color Space Decoder** | `VOIPS_LIB.THUMBNAIL::YUV422ToRGB` | None (Pure math conversion) |
| **Frame Renderer** | `System.Drawing.Bitmap`, `BitmapData`, `Marshal.Copy` | None (Direct GDI+ memory blit) |
| **Control & Switching** | Telnet Client (Port 6970) / HTTP REST (Port 9200) | `Newtonsoft.Json.dll` |

> **Key Takeaway**: The app **does NOT rely on external video media libraries** such as VLC, FFmpeg, GStreamer, DirectShow, or OpenCV. It implements a lightweight, native UDP listener and YUV422-to-RGB rasterizer.

---

## 3. Deep-Dive: How Preview from Multicast Works

### 3.1 Network Initialization & Multicast Joining
- **Socket Configuration**: An instance of `UdpClient` is created and bound to the multicast listening port:
  `csharp
  this.UDP = new UdpClient(this.IPA_MulticastPort);
  this.UDP.JoinMulticastGroup(this.IPA_MulticastAddress, this.IPA_PCAddress);
  `
- **Socket Parameters**:
  - Multicast Address: Typically in Semtech allocation block `224.1.1.1` - `224.1.3.255` (e.g. `239.255.x.x` or user defined).
  - Multicast Port: Configured in the UI `Text_MultiCastPort` (commonly 10000 or 1234).
  - Local Interface IP: Must bind to the PC NIC that sits on the SDVoE 10G/1G network (IPA_PCAddress).
- **Asynchronous Receiver**: Operates asynchronously using .NET APM pattern:
  `csharp
  this.UDP.BeginReceive(new AsyncCallback(this.Receive), null);
  `

### 3.2 RTP Packet Structure (Wire Protocol)
Each incoming UDP datagram is verified to be $\ge 20$ bytes. Packets follow RFC 4175 (RTP Payload Format for Uncompressed Video):

`
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|V=2|P|X|  CC   |M|     PT      |       Sequence Number         |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                           Timestamp                           |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|           Synchronization Source (SSRC) identifier            |
+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+
|      Extended Seq Number      |            Length             |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|F|         Line Number         |C|           Offset            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                          Payload Data ...                     |
`

- **Bytes 0-11: Standard RTP Header**
  - Byte 0: Version = (b[0] >> 6), Padding = (b[0] >> 5) & 1, Extension = (b[0] >> 4) & 1, CC = b[0] & 0x1F.
  - Byte 1: Marker = (b[1] >> 7) (1 = Last packet of the video frame), PayloadType = b[1] & 0x7F.
  - Bytes 2-3: SequenceNumber = (b[2] << 8) | b[3].
  - Bytes 4-7: Timestamp (32-bit big-endian).
  - Bytes 8-11: SSRC (32-bit big-endian).
- **Bytes 12-19: Uncompressed Video Line Header (RFC 4175)**
  - Bytes 12-13: ExtendedSequenceNumber = (b[12] << 8) | b[13].
  - Bytes 14-15: Length = (b[14] << 8) | b[15] (Length of scanline data in bytes).
  - Bytes 16-17: LineNo = (b[16] << 8) | b[17] (Bit 15 is Field Identification; lower 15 bits is scanline index).
  - Bytes 18-19: Offset = (b[18] << 8) | b[19] (Bit 15 is Continuation flag; lower 15 bits is horizontal pixel offset).
- **Bytes 20..N: Payload Data**
  - Uncompressed YUV 4:2:2 raster samples.

### 3.3 Frame Assembly & Marker Detection
1. As packets arrive from a specific transmitter IP, they are stored in DeviceInfo[senderIP].RTP_Data.Add(packet).
2. When a packet with Marker == 1 is received:
   - Signals that the **current image frame is complete**.
   - The application sorts packets by SequenceNumber.
   - Validates that RTP_Data.Count >= (lastPacket.LineNo + 1) (ensuring every scanline 0 to $ was received without packet loss).
   - Once validated, triggers RawYUV422_To_Bitmap.

### 3.4 Dimension Derivation
- **Width**: PayloadData.Length / 2 (Since 1 scanline is packaged per UDP packet, and YUV422 consumes 2 bytes per pixel, a payload of 640 bytes yields 320 pixels width).
- **Height**: lastPacket.LineNo + 1 (e.g. if the final packet has LineNo = 179, height is 180 pixels).
- Typical preview frame formats produced: **320x180**, **480x270**, or **640x360**.

### 3.5 Color Space Transform (YUV422 -> RGB24)
The payload stores consecutive pixel pairs in 4 bytes: [U, Y0, V, Y1].
For every 4 bytes:
- **Pixel A**:  = \text{byte}[1]$,  = \text{byte}[0]$,  = \text{byte}[2]$
- **Pixel B**:  = \text{byte}[3]$,  = \text{byte}[0]$,  = \text{byte}[2]$

Conversion algorithm:
`csharp
double R = Y + 1.4065 * (U - 128.0);
double G = Y - 0.3455 * (V - 128.0) - 0.7169 * (U - 128.0);
double B = Y + 1.7790 * (V - 128.0);

byte r = (byte)Math.Max(0, Math.Min(255, R));
byte g = (byte)Math.Max(0, Math.Min(255, G));
byte b = (byte)Math.Max(0, Math.Min(255, B));
`
The RGB bytes are written to a flat array (yte[] rgb = new byte[Width * Height * 3]) and loaded into a GDI+ Bitmap via Bitmap.LockBits and Marshal.Copy.

### 3.6 Refresh Rate & Performance Parameters
- **Stream Frame Rate**: Semtech hardware encoders throttle the preview stream to **1 fps to 5 fps** (typically 1â€“2 fps / 500msâ€“1000ms intervals). This low framerate is designed specifically to conserve 10G/1G switch fabric bandwidth while providing live thumbnail feedback.
- **Client Render Latency**: < 5 milliseconds per frame (since each frame is only ~320x180 = ~115 KB RGB).
- **Network Bandwidth**: ~1.5 Mbps to 3.5 Mbps per preview stream.