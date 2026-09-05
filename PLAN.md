# University of Lleida TFM Presentation Prototype

## Summary

Create `PRESENTATION.pptx` in English as a custom 16:9 University of Lleida presentation for an approximately 30-minute defense. The total slide count will remain content-driven rather than fixed.

The presentation will use `TFM.docx`, the implemented application, and repository history as primary evidence. It will contain opening and closing slides around five clearly identified main sections:

1. Theory
2. Issues and Iterations
3. Future Work
4. Potential Applications
5. Demo / Video

No application code or public interfaces will be changed.

## Slide Structure

### Opening Slides

- **Title**
  - “WebRTC in a Radio-Controlled Car via 5G”
  - Raul Hidalgo Caballero
  - Master in Computer Engineering
  - Director: David Sarrat González
  - University of Lleida, September 2026

- **Research Question**
  - Can an RC car deliver live video to a remote browser with less than 400 ms glass-to-glass latency while keeping the central server outside the media path?
  - Distinguish the intended 5G context from the prototype’s actual 4G validation.

- **Project Scope**
  - Use this exact scope statement:
    > The project scope is to establish a low-latency video connection between an RC car and a remote web browser, targeting a glass-to-glass latency below 400 ms. Data transport and vehicle control are not included.
  - Reinforce visually with “Video included” and “Data/control excluded.”

- **Presentation Roadmap**
  - Display the five main sections exactly and in order.

### Section 1 — Theory

The explanatory order will be preserved exactly as requested.

- **WebRTC as a Whole**
  - Browser and native APIs, peer connection, signaling, connectivity establishment, encryption, congestion control, and real-time media.
  - Explain that signaling transport is deliberately not prescribed by WebRTC.

- **The WebRTC Protocol Stack**
  - Media capture and playback.
  - Video codec layer.
  - RTP/RTCP and SRTP/SRTCP.
  - DTLS and ICE.
  - STUN/TURN over UDP/IP.
  - Show SCTP/DataChannel only as part of the wider standard and mark it outside project scope.

- **STUN and TURN**
  - STUN discovers a public-facing address and does not relay media.
  - TURN provides a public relay when a direct connection cannot be established.
  - Compare direct and relayed paths, including latency, bandwidth, and infrastructure costs.
  - This theory appears before the SDP offer.

- **SDP Offer and Answer**
  - Media sections and directions, supported codecs, RTP parameters, ICE credentials, and DTLS fingerprints.
  - Project flow: Raspberry Pi creates the offer, SignalR transports it, and the browser returns the answer.

- **ICE Candidates After SDP**
  - Host, server-reflexive, peer-reflexive, and relay candidates.
  - Candidate exchange, connectivity checks, pair priority, nomination, and Trickle ICE.
  - Show the initial SDP exchange followed by trickled ICE candidates, while noting that gathering and SDP creation can overlap at runtime.

- **WebRTC Video Encodings**
  - VP8 and H.264 Constrained Baseline as mandatory-to-implement interoperability choices.
  - VP9 and AV1 as optional modern choices.
  - H.265 as fragmented and unsuitable as a universal browser assumption.
  - Connect the theory to the project’s current MJPEG-to-H.264 pipeline.

- **WHIP and WHEP**
  - WHIP: publisher/device sends media to a central media server.
  - WHEP: browser/viewer receives media from a central media server.
  - Explain that these normally create two server-terminated WebRTC sessions.

- **WHIP/WHEP Concepts Implemented Through SignalR**
  - Use the following statement:
    > I implemented the WHIP and WHEP signaling concepts through SignalR. I did not implement the complete HTTP protocols because WHIP and WHEP are designed for device-to-central-server ingestion and central-server-to-browser egress. This project keeps the central server outside the video path and uses it only for signaling.
  - Map publisher/viewer roles, SDP exchange, ICE exchange, session lifecycle, and termination to SignalR.
  - Clearly state that the project is not WHIP/WHEP protocol-compliant and that the HTTP endpoints remain unimplemented.

### Section 2 — Issues and Iterations

- **Initial Prototype Architecture**
  - RC car/Raspberry Pi and camera.
  - ASP.NET server with SignalR.
  - Remote React browser.
  - STUN and an expected direct WebRTC media path.

- **The NAT Problem**
  - Private IPv4 addresses on both endpoints.
  - Mobile carrier-grade NAT.
  - Browser-side NAT and firewalls.
  - Destination-dependent mappings and blocked inbound UDP.
  - Explain why discovering an address through STUN does not guarantee reachability.

- **Discovery of TURN**
  - STUN alone was insufficient for every network.
  - TURN became the relay fallback tested by ICE.
  - Direct candidates remain preferred; relay candidates improve reliability at an infrastructure and latency cost.

- **Discovery of IPv6**
  - IPv6 may avoid the IPv4 carrier-grade NAT layer.
  - ICE can test IPv6 alongside IPv4 and TURN.
  - IPv6 is not automatically faster and does not remove firewall restrictions.
  - Present it as a promising route that still requires validation.

- **Discovery of WHIP and WHEP**
  - These specifications clarified publisher/viewer roles and signaling lifecycle.
  - Their concepts were adopted through SignalR.
  - Full HTTP protocol implementation was rejected because it would place a central media server in the video path.

- **libcamera vs FFmpeg Shared Libraries vs External FFmpeg RTP Emission**
  - Comparison table:
    - **libcamera:** considered as the native Raspberry Pi camera stack, particularly for CSI cameras; not implemented in the recorded repository. The final Logitech C920 path uses V4L2.
    - **FFmpeg shared libraries:** first implemented through `SIPSorceryMedia.FFmpeg` and FFmpeg.AutoGen; provided direct in-process callbacks but depended on exact FFmpeg 8.1 native-library compatibility.
    - **External FFmpeg RTP emission:** final implementation; avoids binding and ABI fragility by supervising an FFmpeg executable and receiving its encoded output over loopback RTP.
  - Explain that the external process introduces an RTP depacketization/repacketization boundary but creates a clearer deployment boundary.

- **Final Media Pipeline**
  - Diagram:
    - Logitech C920 MJPEG
    - FFmpeg V4L2 capture
    - MJPEG decoding
    - `libx264` H.264 Constrained Baseline encoding
    - RTP over IPv6 loopback
    - .NET RTP parsing and H.264 depacketization
    - Annex-B assembly and SPS/PPS handling
    - SIPSorcery WebRTC/SRTP transmission
    - Browser playback
  - State explicitly: FFmpeg captures and encodes; SIPSorcery owns the WebRTC peer connection.

- **Discovery of CPU Encoding Cost**
  - The Raspberry Pi must decode MJPEG and software-encode H.264.
  - Discuss CPU, memory, thermal, and latency implications qualitatively; do not invent a CPU percentage.
  - Low-latency settings include `superfast`, `zerolatency`, and no B-frames.
  - Clarify that switching to external FFmpeg solved deployment fragility, not encoding cost.

- **Issues with electrical input to the RPI**
  - Talk about the fact that when the RC car takes to much energy, RPI is disconnected for a moment

- **Prototype Result and Limitations**
  - One observed glass-to-glass result of approximately 340 ms.
  - Recorded under the tested 4G configuration.
  - Present it as evidence that the target is feasible, not as a statistical guarantee or completed 5G validation.

### Section 3 — Future Work

- **Complete Remote Control**
  - Talk about achiving full control

- **Hardware and Direct H.264**
  - Remove the MJPEG decode and software `libx264` stage where possible.

- **Network Resilience**
  - Validate IPv6 and dual-stack behavior.
  - Compare direct, IPv6, and relayed ICE paths across realistic NAT types.

- **Protected Electrical Design**
  - Implement complete WHIP/WHEP.

- **ESP32-P4 Integrated Rust Platform**

### Section 4 — Potential Applications

- **A truck that drives alone in Autovias, but a remote driver takes control the lasts moments**

- **Teleoperated Heavy vechicles**
  - Gruas
  - Mineria
  - Barcos

### Section 5 — Demo / Video

- **Demo Setup**
  - Show the physical-car path and the browser-simulator fallback.
  - Identify what the audience should observe: SignalR connection, SDP offer/answer, ICE candidate types, connection status, and live video.

- **Live Demo Sequence**
  - Start the central server and browser interface.
  - Connect the device or simulator.
  - Exchange SDP and ICE through SignalR.
  - Display the selected candidate path.
  - Start the video and demonstrate the latency target.
  - Do not present data or vehicle controls as part of the project scope.

- **Fallback Video**
  - Reserve a large 16:9 media frame for a 45–60 second RC-car demonstration recording.
  - Because no real RC-car recording currently exists in the repository, the prototype will use a clearly labelled video placeholder.
  - The existing simulator countdown video may support the technical fallback but will not be represented as physical-car evidence.

- **Final Takeaways and Questions**
  - WebRTC can support the targeted low-latency RC-car video link.
  - NAT traversal and software encoding were the primary engineering challenges.
  - Hardware H.264, broader network testing, and production relay infrastructure are the next priorities.

## Visual and Source Treatment

- Use official UdL burgundy `#830051`, black, white, and light grey with the official logo.
- Use Arial with approximately 42 pt titles, 32 pt section headings, and at least 18 pt body text.
- Prefer editable diagrams, timelines, topology illustrations, and comparison tables over text-heavy slides.
- Add concise speaker notes and source references from `TFM.docx`, repository history, relevant RFCs, WHIP RFC 9725, and the active WHEP draft.
- Allocate approximately 3 minutes to opening, 9 minutes to theory, 10 minutes to issues and iterations, 3 minutes to future work, 2 minutes to applications, and 3 minutes to demo and closing.

## Validation

- Render every slide and inspect a contact sheet and full-size slide images for overflow, overlap, clipping, weak contrast, and inconsistent spacing.
- Confirm the five main sections are visible and ordered correctly.
- Confirm the Theory sequence is WebRTC, stack, STUN/TURN, SDP, ICE, codecs, and WHIP/WHEP.
- Confirm the initial architecture contains STUN but no TURN.
- Confirm the media iteration uses “external FFmpeg RTP emission” and does not claim FFmpeg establishes WebRTC.
- Confirm libcamera is described as considered rather than repository-implemented.
- Confirm the full pipeline includes MJPEG decoding and software H.264 encoding.
- Confirm WHIP/WHEP are described as concepts implemented through SignalR, not completed protocols.
- Confirm data and vehicle control remain outside scope.
- Confirm 340 ms is described as one 4G observation rather than a general or 5G result.
- Open the final file in PowerPoint and verify fonts, diagrams, notes, links, and demo/video placeholders.
