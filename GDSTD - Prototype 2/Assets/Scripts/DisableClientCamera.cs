using UnityEngine;
using Mirror;

public class DisableClientCamera : NetworkBehaviour {
	public Camera playerCam;

	private void Update() {
		if (!isLocalPlayer) {
			playerCam.enabled = false;
			return;
		}
	}
}
