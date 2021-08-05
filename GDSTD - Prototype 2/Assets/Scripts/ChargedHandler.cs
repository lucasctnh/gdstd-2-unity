using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Mirror;

public class ChargedHandler : NetworkBehaviour {
	[Header("Properties")]
	[SyncVar] public string spawnedBy;
	public GameObject target;

	[Header("In-Game Variables")]
	[Tooltip("The projectile's speed.")]
	public float speed = 50f;

	[Header("In-Script Variables")]
	private GameManager _gameManager;

	private void Start() {
		_gameManager = GameObject.Find("Game Manager").GetComponent<GameManager>();

		if (spawnedBy == "hostClient") target = _gameManager.justClient;
		else target = _gameManager.hostClient;
	}

	private void Update() {
		transform.position += transform.forward * speed * Time.deltaTime;
	}

	private void OnTriggerEnter(Collider other) {
		if (other.gameObject.CompareTag("Player") && other.gameObject == target) {
			NetworkManager.singleton.ServerChangeScene(SceneManager.GetActiveScene().name);
		}
		else if (other.gameObject.CompareTag("OutBounds"))
			NetworkServer.Destroy(gameObject);
	}
}
