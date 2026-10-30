# Chair Lift

Ride the map's chair lifts.

- At the bottom of a chair lift a prompt shows up. Press **A** (controller), or **G** on the
  keyboard, and you get on the next chair heading up.
- Each chair has four seats lined up along the bench, so you and three friends (each with the mod)
  can ride together. You take the free seat nearest to where you're standing.
- You sit down, looking up the lift, and can open the phone on the way up: **Y** or **H** (press
  again to put it away). While the phone is open, A only works the phone.
- Near the top you get off before the wheel and slide onto the snow. Press A again any time to jump
  off early. T-bars and rope tows work as normal.

## Settings

Buttons, getting off automatically and how far before the top wheel, sitting (on/off, hip and knee
bend, live while riding) and the seat fit (height, forward/back, spacing) in Options > Mods >
Chair Lift.

## How it works

| File | What it does |
| --- | --- |
| [ChairLiftMod](ChairLiftMod.cs) | Settings, the frame and physics-step hooks, buttons, the prompt |
| [RideRuntime](RideRuntime.cs) | Getting on, riding, getting off, the phone |
| [Seating](Seating.cs) | Seat layout, which lifts are chair lifts, the sitting pose, holding maths (tested) |
| [Integration/GameLifts](Integration/GameLifts.cs) | The game's `LiftLine`s, stations and carriers |
| [Integration/PhysicsHold](Integration/PhysicsHold.cs) | Holding the rider in MuJoCo; the chair's collisions |
| [Integration/Rider](Integration/Rider.cs) | Placing, sitting, looking ahead, the phone flag |

The rider's `daw()` puts the whole body back in its default stance, so it's only used to sit down.
After that the free joints of the rider and its gear are moved to the seat and given the chair's
speed every physics step with MuJoCo's `mj_getState`/`mj_setState`, leaving the joints to the
sitting pose. The chair's collision shapes are switched off while seated (they're static world
shapes, picked by being under the carrier's object). Everything written to the physics is tied to
the scene's model and dropped when the game rebuilds it.
