# CHARACTER_MOTION_SPEC.md

## Goal

Build a fresh **Overgrowth-style procedural locomotion** foundation for Third Horizon / TowerOfBaby.

The movement should be:

- responsive
- slick
- readable
- terrain-aware
- procedural-first
- extensible to new skeletons and future mechanics

This is **not** a physics-simulation project first, and it is **not** an AI/ML motion project first.

We want a strong handcrafted procedural control foundation now, while leaving the door open for data-driven or AI-assisted motion later.

---

## Immediate style target

**Primary reference:** Overgrowth-style procedural motion feel.

Interpret that as:

- body movement is intentional and readable
- feet support locomotion convincingly
- terrain adaptation matters
- motion looks alive without relying on big authored animation sets
- simple layered control rules beat overcomplicated “smart” systems

---

## Long-term note

Future AI/data-driven motion is allowed as a later extension.

Examples of future-compatible directions:
- mocap-informed tuning
- PCA / parameter-space body and motion synthesis
- learned terrain adaptation
- motion priors or style layers

But none of that should shape Milestone 1 architecture beyond keeping things modular and explicit.

---

## Core locomotion philosophy

### 1. Root/body movement is authoritative
The root/body decides intended movement direction, speed, and facing.

### 2. Feet are support solvers
Feet do not invent locomotion. They support and stabilize body-driven movement.

### 3. Steps are event-driven
A foot should step because support is needed, not because an oscillator or gait clock is “in charge.”

### 4. Terrain awareness comes from support planning
Use terrain sampling / raycasts / contact probing to choose good support targets.

### 5. Swing is explicit
A stepping foot follows a simple deliberate path from current support to target support.

### 6. Pelvis and torso are secondary
They add polish and stability, but must never break foot correctness.

### 7. Polish is additive
Bounce, foot roll, torso sway, anticipation, and style should be layered on top of a correct basic stepper.

---

## Non-goals for the fresh rewrite

Do **not** build any of the following in Milestone 1:

- full physics locomotion
- phase-clock-driven gait logic as the source of truth
- animation clips / keyframed locomotion
- motion matching
- ML / learned controllers
- advanced balance solvers
- combat motion
- jumping
- running
- nonhuman skeleton support beyond keeping abstractions general

---

## Milestone 1: Grounded walk only

Build a fresh biped walker with only these responsibilities:

### Required behavior
- stable forward walking on flat terrain
- stable forward walking on mild slopes / uneven terrain
- no leg trailing far behind the body
- no bouncing-in-place or decorative stepping
- readable left/right mirrored stepping
- clear foot placement targets
- clear planted vs stepping states

### Required system shape
Each foot has only two states:
- **planted**
- **stepping**

Each planted foot has:
- a support/home region relative to the moving body
- a trigger for when support is no longer good enough

A step begins when:
- planted foot exceeds geometric support thresholds
- or the current support point is no longer good for the current body motion

A new support target is chosen from:
- projected root motion
- stance width
- desired facing / movement direction
- terrain sample / raycast

Swing path is:
- current foot position -> target foot position
- fixed or simply parameterized duration
- smooth horizontal interpolation
- modest vertical arc
- no fancy flourish yet

Pelvis / torso:
- follow locomotion support
- add limited stabilization / polish
- never override foot correctness

---

## Suggested architecture

### MovementIntent
High-level desired:
- move direction
- move speed
- facing direction
- locomotion mode

### RootMotionDriver
Owns:
- body translation
- body orientation
- lean
- speed response

### FootPlanner
Owns:
- planted vs stepping
- support region / home position
- deciding when a step is needed
- choosing next support target

### FootSwingSolver
Owns:
- moving foot from start to target
- step arc
- step duration

### BodyPoseSolver
Owns:
- pelvis offset
- leg IK
- torso stabilization
- optional arm balance

### MotionDebug / Metrics
Owns:
- debug draw
- step event logging
- support threshold inspection
- foot skate measurement

---

## Required debug / profiler outputs

For each foot, expose:

- planted or stepping state
- current support/home target
- current planted position
- threshold violation amount
- chosen next target
- terrain normal under target
- foot skate distance during plant
- step duration

Also expose:

- root desired movement vector
- root actual movement vector
- body facing vector
- stance width
- slope / terrain sample information used by planner

Debugging must make it easy to answer:
- why did this foot step?
- why did it step there?
- why is it still planted?
- why is it skating?

---

## Milestone roadmap after grounded walk

### Milestone 2
Run as a parameterized extension of the same locomotion core.

### Milestone 3
Jump with:
- crouch / compression
- takeoff
- airborne pose
- landing
- recovery

### Milestone 4
Sword swing layered onto locomotion.

### Milestone 5
Tie blade sweep into terrain interaction / deformation.

### Milestone 6+
Combat-aware footing, broader skeleton support, and eventually voxel/body-damage integration.

---

## Practical repo reset guidance

Keep:
- terrain systems
- game/world/bootstrap systems
- debug/profiling systems that are not tightly coupled to old humanoid code
- UI overlay and general scene setup if convenient

Replace or rebuild:
- humanoid locomotion implementation
- humanoid runtime/controller logic
- humanoid rig/body-generation logic if it is tightly coupled to the failed locomotion assumptions
- humanoid scene wiring that depends on old scripts

Prefer preserving the terrain/game shell and swapping in a new character stack.

---

## Codex implementation guidance

When generating the new system:

- start small
- build Milestone 1 only
- do not resurrect old phase-driven gait logic
- do not do broad speculative refactors
- prioritize correctness and clarity over cleverness
- keep files small and responsibilities explicit
- include debug visualization from the start

Success is:
- a simple, believable, extensible walker
- not an ambitious but unstable locomotion experiment
