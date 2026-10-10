// First-person movement: mouse look, WASD, Shift to sprint, Space for a small
// hop. You can walk on land and docks (or the trawler's deck at sea) but not
// into the water or through tree trunks. With a line out you can still walk,
// slowly, and not sprint.

using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LastCast
{
    public class PlayerController : MonoBehaviour
    {
        public const float EyeHeight = 1.65f;
        const float Radius = 0.35f; // metres
        const float Gravity = 22f; // a little stronger than real, so hops feel snappy
        const float JumpHeight = 0.45f; // metres: a small hop

        public Camera Cam { get; private set; }
        public bool Sprinting { get; private set; }
        public bool Grounded { get; private set; } = true;
        public bool CanMove = true;
        /// <summary>While set, you're riding something (the boat at the dock): you stay at this point and can only look around.</summary>
        public Func<Vector3> Anchor;
        public static float Sensitivity = 0.11f; // degrees per mouse pixel
        /// <summary>Camera shake (a boss slamming the deck), dying away by itself.</summary>
        public static float Shake;

        float yaw, pitch, feetY, vy, bob;

        public float Yaw => yaw;
        /// <summary>The step cycle (radians), and how much you're walking (0 standing still, 1 striding), for the rod's sway.</summary>
        public float Bob => bob;
        public float Striding { get; private set; }
        public void SetPitch(float p) => pitch = p;

        /// <summary>Turn to look at a point (AutoShot).</summary>
        public void Face(Vector3 target)
        {
            var d = target - Cam.transform.position;
            yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            pitch = Mathf.Clamp(-Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg, -85, 85);
        }

        public void Init(Vector3 position, float facing)
        {
            Cam = GetComponentInChildren<Camera>();
            Teleport(position, facing);
        }

        /// <summary>Put the angler somewhere new (standing on whatever is there), facing a direction.</summary>
        public void Teleport(Vector3 position, float facing)
        {
            feetY = Place.StandHeight(position);
            vy = 0;
            Grounded = true;
            transform.position = new Vector3(position.x, feetY, position.z);
            yaw = facing;
        }

        /// <summary>Where you stand, in world units (the web game's coordinates).</summary>
        public Vector2 WorldPos => WorldMap.I.ToWorld(transform.position);

        void Update()
        {
            if (Cam == null) return;
            float dt = Time.deltaTime;
            bool menu = Game.I.MenuOpen || AutoShot.Active || NetTest.Active;
            Cursor.lockState = menu ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = menu;

            var mouse = Mouse.current;
            if (!menu && mouse != null)
            {
                var d = mouse.delta.ReadValue();
                yaw += d.x * Sensitivity;
                pitch = Mathf.Clamp(pitch - d.y * Sensitivity, -85, 85);
            }

            if (Anchor != null)
            {
                var at = Anchor();
                transform.position = at;
                feetY = at.y;
                vy = 0;
                Grounded = true;
                Sprinting = false;
                bob = 0;
                Striding = 0;
                Look();
                return;
            }

            var kb = Keyboard.current;
            Vector2 input = Vector2.zero;
            bool wantSprint = false, wantJump = false;
            if (!menu && kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) input.y += 1;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) input.y -= 1;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) input.x += 1;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) input.x -= 1;
                wantSprint = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
                wantJump = kb.spaceKey.wasPressedThisFrame;
            }

            // A line out (or a cast being charged) slows you to a careful walk.
            var f = Game.I.Fishing;
            bool fishing = f != null && (f.State != LineState.Idle || f.Charge >= 0);
            bool moved = false;
            Sprinting = false;
            if (CanMove && input != Vector2.zero)
            {
                Sprinting = wantSprint && !fishing;
                float speed = Tuning.WalkSpeed * (Sprinting ? Tuning.SprintSpeed : 1) * (fishing ? Tuning.FishingWalk : 1);
                var fwd = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
                var right = Quaternion.Euler(0, yaw, 0) * Vector3.right;
                var step = (fwd * input.y + right * input.x).normalized * speed * dt;
                moved = Move(step);
                if (!moved) Sprinting = false;
            }

            // Feet: follow the ground smoothly, or fly through a hop.
            float ground = Place.StandHeight(transform.position);
            if (wantJump && Grounded && CanMove)
            {
                vy = Mathf.Sqrt(2 * Gravity * JumpHeight);
                Grounded = false;
            }
            if (Grounded)
            {
                feetY = Mathf.Lerp(feetY, ground, 1 - Mathf.Exp(-14 * dt));
            }
            else
            {
                vy -= Gravity * dt;
                feetY += vy * dt;
                if (feetY <= ground && vy <= 0)
                {
                    feetY = ground;
                    vy = 0;
                    Grounded = true;
                }
            }
            transform.position = new Vector3(transform.position.x, feetY, transform.position.z);
            bool walking = moved && Grounded;
            Striding = Mathf.MoveTowards(Striding, walking ? (Sprinting ? 1.4f : 1) : 0, dt * 5);
            bob = walking ? bob + dt * (Sprinting ? 13 : fishing ? 6 : 9) : Mathf.Lerp(bob, Mathf.Round(bob / Mathf.PI) * Mathf.PI, dt * 6);
            Look();
        }

        void Look()
        {
            transform.rotation = Quaternion.Euler(0, yaw, 0);
            var jolt = Shake > 0 && GraphicsOptions.CameraShake ? UnityEngine.Random.insideUnitSphere * Shake * 0.12f : Vector3.zero;
            Shake = Mathf.Max(0, Shake - Time.deltaTime * 1.2f);
            Cam.transform.localPosition = new Vector3(0, EyeHeight + (GraphicsOptions.HeadBob ? Mathf.Sin(bob) * 0.04f : 0), 0) + jolt;
            Cam.transform.localRotation = Quaternion.Euler(pitch + jolt.y * 8, jolt.x * 6, jolt.z * 6);
        }

        /// <summary>Slide along whatever blocks you (water, stalls, trunks, the rail), one axis at a time, as the web game does.</summary>
        bool Move(Vector3 step)
        {
            var p = transform.position;
            var start = p;
            // Somehow inside a trunk already (an old save in a tree): ignore trunks until you're out.
            bool stuck = !Place.IsWalkable(p);
            bool Ok(Vector3 q, float dx, float dz)
            {
                if (!stuck) return Walkable(q, dx, dz);
                if (Place.AtSea) return SeaWorld.IsWalkable(q);
                var w = WorldMap.I.ToWorld(q);
                return WorldMap.I.IsWalkable(w.x, w.y);
            }
            var tryX = p + new Vector3(step.x, 0, 0);
            if (Ok(tryX, step.x, 0)) p = tryX;
            var tryZ = p + new Vector3(0, 0, step.z);
            if (Ok(tryZ, 0, step.z)) p = tryZ;
            transform.position = p;
            return (p - start).sqrMagnitude > 1e-8f;
        }

        bool Walkable(Vector3 p, float dx, float dz)
        {
            // Check the point you'd be at, plus your edge in the direction you move.
            var edge = p + new Vector3(Mathf.Sign(dx) * (dx != 0 ? Radius : 0), 0, Mathf.Sign(dz) * (dz != 0 ? Radius : 0));
            return Place.IsWalkable(p) && Place.IsWalkable(edge);
        }

        /// <summary>The horizontal direction you're looking (Unity space).</summary>
        public Vector3 Forward => Quaternion.Euler(0, yaw, 0) * Vector3.forward;
        public float Pitch => pitch;
    }
}
