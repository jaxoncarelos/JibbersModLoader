# Phone Camera

Film your mates through your phone.

- Open your phone: there's a **Camera** button (or press **K**). A phone appears on screen with
  the camera's live picture, held up in front of your rider's face.
- It follows the mate you pick and keeps them framed, zooming in as they ride away and out as they
  come close, with a little handheld wobble.
- Look around with the mouse or the right stick; zoom from 0.1x (super wide) to 8x; flip to a
  selfie; turn the phone upright for vertical video.
- Press the phone's red button to record. The clip is exactly what the phone films, saved as an MP4
  in `Videos\Jibbers Clips` (540p to 1440p, 30 fps, no sound).
- While filming your rider holds the phone up (your own pose if you saved one called
  `HoldPhonecamera` in the game's pose editor) and can be hidden, clothes and all.

## Controls (while the camera is open)

| | Keyboard / mouse | Xbox | PlayStation |
| --- | --- | --- | --- |
| Look around | Mouse | Right stick | Right stick |
| Re-aim at your mate | R / middle mouse | R3 | R3 |
| Next / previous mate | E / Q | RB / LB | R1 / L1 |
| Zoom in / out | C / Z | D-pad up / down | D-pad up / down |
| Selfie | F | Y | Triangle |
| Portrait / landscape | B | D-pad right | D-pad right |
| Record | Click the red button, or T | X | Square |
| Close | Esc | Back (View) | Share (Create) |

PlayStation controllers work with or without Steam Input. Esc also opens the game's pause menu; the
mod puts it away again when Esc closed the camera.

## Settings

Options > Mods > Phone Camera: your saved pose, hide your rider, first person (and whether it follows
mates), clip quality, start in portrait, phone in the middle, head follows the camera, motion blur
around the phone and its strength, look speed and inversion, the controls hint, controller on/off,
and every key.

## How it works

| File | What it does |
| --- | --- |
| [PhoneCameraMod](PhoneCameraMod.cs) | Settings, the FollowCam.LateUpdate hook, controls, the pause / replay events |
| [CamMath](CamMath.cs) | Aiming, smoothing, turning, lens zoom, screen sizes (tested) |
| [PoseFile](PoseFile.cs) | Reading poses saved by the game's pose editor (tested) |
| [PhoneArt](PhoneArt.cs) | Drawing the phone's body and record button (tested) |
| [Integration/CameraRig](Integration/CameraRig.cs) | Our camera into a RenderTexture, following, first person, selfie |
| [Integration/PhoneScreen](Integration/PhoneScreen.cs) | The phone on screen: picture, body, status bar, hint |
| [Integration/PhoneButton](Integration/PhoneButton.cs) | The Camera button on the game's phone |
| [Integration/Holding](Integration/Holding.cs) | The pose, the phone in the hand, hiding, first person, looking |
| [Integration/MotionBlur](Integration/MotionBlur.cs) | A URP Volume with only MotionBlur, on while filming |
| [Integration/ClipRecorder](Integration/ClipRecorder.cs) | MP4 recording with Windows' Media Foundation encoder |
| [Integration/Pads](Integration/Pads.cs) | Xbox pads by XInput, PlayStation pads through Rewired |

Notes for reviewers: native code is Media Foundation's sink writer (`mfplat`, `mfreadwrite`,
`ole32`), called through its COM method slots on the recorder's own thread, and nothing else. The
game camera is never changed except the follow camera's own first-person fields, which are put back
when the camera closes. The mod only writes files in `Videos\Jibbers Clips`.
