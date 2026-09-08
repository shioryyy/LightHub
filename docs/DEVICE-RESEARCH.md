# Public device research and iteration plan

Reviewed on 2026-09-07. Public sources are useful without owning every model, but do
not establish that LightHub safely writes a particular device or firmware.

## Sources

| Source | Useful information | Limits |
| --- | --- | --- |
| [libratbag device catalog](https://github.com/libratbag/libratbag/tree/b8d4d3ca1f4d6b23c664ffee2888b8eb669bee21/data/devices) | Model IDs, driver selection and model-specific quirks | Not a complete memory dump or LightHub validation |
| [libratbag HID++ driver](https://github.com/libratbag/libratbag/blob/b8d4d3ca1f4d6b23c664ffee2888b8eb669bee21/src/driver-hidpp20.c) | Feature capability handling and onboard-profile behavior | Check firmware differences and source licenses before reusing code |
| [Piper diagrams](https://github.com/libratbag/piper/tree/5d3c8845b55643595ccb8029dfa5c7a2fb079e77/data/svgs) | Physical button positions and button indices | Diagrams do not prove input behavior or write support; artwork licensing is separate |
| [Solaar receiver library](https://github.com/pwr-Solaar/Solaar/tree/master/lib/logitech_receiver) | Feature IDs, settings, identity, batteries and receiver handling | Different feature coverage and licensing; not a drop-in C# driver |
| [OpenLogi](https://github.com/AprilNEA/OpenLogi) | Native UI workflows, current DPI and device capability handling | Primarily an Options+ alternative; many remappings use OS hooks and resident behavior |
| [better-logihub](https://github.com/cUDGk/better-logihub) | Existing local reference for HID++ and onboard encoding | Its 141 local catalog entries are metadata, not 141 tested LightHub devices |

No external implementation or proprietary G HUB device assets were imported in this
iteration. Existing LightHub procedural artwork is retained. The GPW location facts
were independently represented in the UI.

## Concrete model evidence found

The following IDs and quirks were read from the pinned libratbag catalog above.

| Model | Mouse IDs | Catalog details | LightHub status |
| --- | --- | --- | --- |
| G PRO Wireless | 4079, C088 | hidpp20, DeviceIndex=1 | One Windows unit tested; experimental |
| G305 / G304 family | 4074 | G305 entry sets Leds=0 and Quirk=G305 | Identification only, read-only |
| G502 HERO | C08B | Quirk=INDEX_OFFSET | Identification only, read-only |
| G PRO X Superlight | 4093, C094 | hidpp20 | Identification only, read-only |

The differences matter: adding an ID must not bypass quirks, feature discovery,
memory-layout checks or the platform evidence rule. G304 regional naming should also
be confirmed by contributors on actual hardware.

## GPW1 button locations

Source: [Piper GPW diagram](https://github.com/libratbag/piper/blob/5d3c8845b55643595ccb8029dfa5c7a2fb079e77/data/svgs/logitech-g-pro-wireless.svg).
Piper's button0..button7 correspond to LightHub's display numbers 1..8.

| Display number | Physical position |
| --- | --- |
| 1 | Left main button |
| 2 | Right main button |
| 3 | Wheel click |
| 4 | Left rear side button |
| 5 | Left front side button |
| 6 | Underside DPI button |
| 7 | Right rear side button |
| 8 | Right front side button |

Position names never come from the current action. Remapping a side button to a
keyboard shortcut must not change its physical label. Unknown layouts retain neutral
numbered labels. Covered or removed side buttons cannot be inferred from profile bytes.

## Next iterations

1. Finish GPW1 interaction validation: physical buttons, temporary DPI across profile
   switching/sleep, persistent settings across power cycles, hotplug and wired mode.
2. Keep current sensor controls separate from flash saves. Current DPI already uses
   0x2201 with read-back; do not automatically write flash for every slider movement.
   Next measure complete GUI latency and consider a serialized reusable session with
   reconnect invalidation and coalesced updates if connection setup is significant.
3. Add named local presets and clearer change summaries. Review before importing a
   preset into a different model; do not apply another device's raw memory backup.
4. Gather contributor reports for G305/G304, G502 HERO and Superlight: redacted identity,
   firmware, feature table, memory geometry, capabilities and physical button mapping.
   Keep raw backups local. Build synthetic fixtures from documented structures.
5. Enable additional writes only after per-model/firmware read-back and restore evidence.
   Add RGB, G-Shift, macros and high-rate extensions as separately tested capabilities.

Per-application switching and complex software macros need a separate product decision:
they can require resident processes and input hooks, unlike the current onboard-only
mapping approach. OpenLogi's larger feature list does not remove that tradeoff.
