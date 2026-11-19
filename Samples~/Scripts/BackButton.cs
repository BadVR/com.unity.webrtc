using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem;

namespace Unity.WebRTC.Samples
{
    internal class BackButton : MonoBehaviour
    {
        [SerializeField]
        GameObject m_BackButton;
        public GameObject backButton
        {
            get => m_BackButton;
            set => m_BackButton = value;
        }

        void Start()
        {
            if (Application.CanStreamedLevelBeLoaded("Menu"))
            {
                m_BackButton.SetActive(true);
            }
        }

		void Update()
		{
			Keyboard keyboard = Keyboard.current;
			if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
			{
				BackButtonPressed();
			}
		}

        public void BackButtonPressed()
        {
            if (Application.CanStreamedLevelBeLoaded("Menu"))
            {
                SceneManager.LoadScene("Menu", LoadSceneMode.Single);
            }
        }
    }
}
