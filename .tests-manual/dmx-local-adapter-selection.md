---
id: dmx-local-adapter-selection
title: DMX Saved and Effective Local Addresses
scope: io-dmx
tags: [dev, essential, edge]
added: 2026-11-11
added-in-version: 4.4
prerequisites:
  - The _agentTests project is open; no production lighting equipment is connected.
  - An ArtnetOutput and SacnOutput each have a list of channel values connected and SendTrigger disabled.
  - The tester knows an active adapter's IPv4 address and subnet mask; no OS adapter changes are needed.
related-help:
  - ../.help/docs/operators/lib/io/dmx/ArtnetOutput.md
  - ../.help/docs/operators/lib/io/dmx/SacnOutput.md
---

Repeat for both operators. Use `LocalAdapterResolverTests` for network layouts you cannot test locally.

## Step: Save an explicit selection

**Action:** Pick a local IP from the dropdown, then save and reopen.

**Expected:** The saved IP is used as-is, even if other addresses share its subnet.

## Step: Load a project from another machine

**Action:** Open a project with a saved IP that isn't local. Try a unique match on its subnet; then try one on the target subnet with no match on the saved subnet.

**Expected:** Each fallback uses the local subnet mask. The dropdown and status show both addresses; the saved IP is unchanged.

## Step: Reject ambiguity and missing matches

**Action:** Try multiple matches on the saved subnet despite a unique target match, multiple target matches with no saved-subnet match, and no matches at all.

**Expected:** Each case warns and blocks output. Invalid masks and `/0` do not qualify.

## Step: Check special values and explicit selection

**Action:** Try an empty or invalid local IP, `0.0.0.0`, and `127.0.0.1`. Pick a local IP after a fallback.

**Expected:** Empty or invalid stays unbound; `0.0.0.0` uses OS routing; loopback works. Picking an IP replaces the saved value.

## Step: Refresh selection explicitly

**Action:** Change the adapter snapshot in `LocalAdapterResolverTests`. Open the dropdown, then press **Reconnect**. Also test an input change and a failed refresh.

**Expected:** Opening the dropdown refreshes only its choices. **Reconnect** or an IP input change reruns selection. A failed refresh clears the old match and warns.

## Step: Send real loopback UDP

**Action:** Listen on UDP `6454` (Art-Net) or `5568` (sACN). Set local and target IP to `127.0.0.1` and enable unicast and **Send Trigger**.

**Expected:** The receiver gets the configured channel values. Disable **Send Trigger** afterward.
