using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SofaUnity
{
    public class SofaKeyEvent : MonoBehaviour
    {
        /// Pointer to the Sofa context this GameObject belongs to.
        public SofaUnity.SofaContext m_sofaContext = null;

        public bool m_isListening = false;

        /// Minimum real-time seconds between two successive key-driven insertion
        /// steps while a key is held. Without this, Update() fires once per
        /// rendered frame, so holding a key drives insertion speed at
        /// (step * frameRate) units/sec - uncapped and framerate-dependent,
        /// which can outrun what the collision/contact solver can resolve
        /// and cause the catheter to tunnel through the vessel wall.
        public float m_keyRepeatInterval = 0.025f;

        /// Rotation deserves its own (longer) cooldown: the catheter tip has a
        /// preformed curve (RodSpireSection), so a small roll angle sweeps the
        /// curved tip through a comparatively large arc/linear distance -
        /// much more than the same-sized translation step moves the tip.
        public float m_rotationRepeatInterval = 0.5f;

        float m_nextAllowedTime = 0f;
        float m_nextAllowedRotTime = 0f;

        // Start is called before the first frame update
        void Start()
        {
            if (m_sofaContext == null)
            {
                GameObject _contextObject = GameObject.FindGameObjectWithTag("GameController");
                if (_contextObject != null)
                {
                    // Get Sofa context
                    m_sofaContext = _contextObject.GetComponent<SofaUnity.SofaContext>();
                }
                else
                {
                    Debug.LogError("RayCaster::loadContext - No SofaContext found.");
                    return;
                }
            }
        }

        // Update is called once per frame
        void Update()
        {
            if (m_isListening == false || m_sofaContext == null)
                return;

            bool anyRotateKey = Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.RightArrow);
            bool anyMoveKey = Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.DownArrow);

            if (anyRotateKey && Time.time >= m_nextAllowedRotTime)
            {
                m_nextAllowedRotTime = Time.time + m_rotationRepeatInterval;

                if (Input.GetKey(KeyCode.LeftArrow))
                    m_sofaContext.SofaKeyPressEvent(18);

                if (Input.GetKey(KeyCode.RightArrow))
                    m_sofaContext.SofaKeyPressEvent(20);
            }

            if (anyMoveKey && Time.time >= m_nextAllowedTime)
            {
                m_nextAllowedTime = Time.time + m_keyRepeatInterval;

                if (Input.GetKey(KeyCode.UpArrow))
                    m_sofaContext.SofaKeyPressEvent(19);

                if (Input.GetKey(KeyCode.DownArrow))
                    m_sofaContext.SofaKeyPressEvent(21);
            }


            if (Input.GetKeyUp(KeyCode.LeftArrow))
                m_sofaContext.SofaKeyReleaseEvent(18);

            if (Input.GetKeyUp(KeyCode.RightArrow))
                m_sofaContext.SofaKeyReleaseEvent(20);

            if (Input.GetKeyUp(KeyCode.UpArrow))
                m_sofaContext.SofaKeyReleaseEvent(19);

            if (Input.GetKeyUp(KeyCode.DownArrow))
                m_sofaContext.SofaKeyReleaseEvent(21);
        }
    }
}
