# Cloudflare Worker & TURN Server Usage Guide

This project relies on WebRTC to create real-time peer-to-peer connections between the Web, Unity, and VR headsets. Because WebRTC connections can sometimes be blocked by strict firewalls or CGNAT networks, a TURN (Traversal Using Relays around NAT) server is used as a fallback to ensure 100% connectivity.

To keep this completely free, we use a **Cloudflare Worker** to dynamically generate time-limited authentication credentials for a free TURN server (Metered.ca / OpenRelay).

Here is the full breakdown of how it works and what it costs:

## 1. The Cloudflare Worker (Free Plan)

**What it does:**  
The Cloudflare Worker **does not relay any video traffic**. It is simply a lightweight API endpoint that safely generates a secure, time-limited token for the TURN server. This prevents bad actors from stealing your TURN server credentials.

**Usage Limits:**  
The free tier of Cloudflare Workers gives you **100,000 requests per day**.

**Real World Impact:**  
A request to the worker is only made *once* when a user joins a room. This means you can have **100,000 sessions or patients join per day completely for free**. It is highly unlikely you will ever need to upgrade to the $5/month paid plan (which gives 10 million requests/month).

## 2. The TURN Relay Server (Metered.ca / OpenRelay)

**What it does:**  
This is the actual server that routes the heavy video and audio traffic, but **only when the direct Wi-Fi P2P connection fails** (e.g., restricted corporate networks or CGNAT). Most calls will connect directly P2P and use 0 bytes of TURN data.

**Usage Limits:**  
The free tier of Metered gives you **50 GB of data per month**.

**Real World Impact:**  
A video call uses roughly 500 MB per hour. Since TURN is only used as a fallback, that 50 GB gives you about **100 hours of relayed fallback video per month**. Since the vast majority of calls will use direct P2P (which uses your own Wi-Fi bandwidth for free), this is a very generous allowance.

## Instructions for Setting Up Cloudflare

If you are setting this project up from scratch, here is how to configure the Cloudflare Worker:

1. Create a free [Cloudflare](https://dash.cloudflare.com/) account.
2. Go to **Workers & Pages** and create a new Worker.
3. Paste the provided Worker script (`cloudflare-worker/worker.js`) into the Cloudflare Worker code editor and deploy it.
4. Open `config.js` in this project.
5. Set `turnMode: 'cloudflare'`.
6. Paste the resulting `https://your-worker.workers.dev` URL into `turnCredentialEndpoint`.

You are now fully configured for cross-network WebRTC!
