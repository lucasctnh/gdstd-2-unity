using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Mirror;

public class ProjectileHandler : NetworkBehaviour {
	[Header("Properties")]
	[SyncVar] public string spawnedBy;
	public GameObject target;

	[Header("In-Game Variables")]
	[Tooltip("The projectile's speed.")]
	public float speed = .5f;
	[Tooltip("The projectile's rotation speed.")]
	public float damping = .5f;
	public float shootForce = 10f;

	[Header("In-Script Variables")]
	private GameManager _gameManager;
	private bool _onTrack = true;

	private void Start() {
		_gameManager = GameObject.Find("Game Manager").GetComponent<GameManager>();

		if (spawnedBy == "hostClient") target = _gameManager.justClient;
		else target = _gameManager.hostClient;
	}

	private void Update() {
		Vector3 targetPosition = target.transform.position;

		// Rotation
		Vector3 lookDir = (targetPosition - transform.position).normalized;
		Quaternion rotation = Quaternion.LookRotation(lookDir);
		Quaternion slerpedRotation = Quaternion.Slerp(
			transform.rotation,
			rotation,
			Time.deltaTime * damping
		);

		// Movement
		float angle = Quaternion.Angle(transform.rotation, rotation);
		if (angle < 45f && _onTrack) {
			transform.rotation = slerpedRotation;
			transform.position = Vector3.MoveTowards(transform.position, targetPosition, speed * Time.deltaTime);
		} else {
			_onTrack = false;
			transform.position += transform.forward * speed * Time.deltaTime;
		}
	}

	private void OnTriggerEnter(Collider other) {
		if (other.gameObject.CompareTag("Player") && other.gameObject == target) {
			other.gameObject.GetComponent<Rigidbody>().AddForce(transform.forward * shootForce, ForceMode.Impulse);
			NetworkServer.Destroy(gameObject);
		}
		else if (other.gameObject.CompareTag("OutBounds"))
			NetworkServer.Destroy(gameObject);
		else if (other.gameObject.CompareTag("Projectile")) {
			NetworkServer.Destroy(gameObject);
			NetworkServer.Destroy(other.gameObject);
		}
	}
}
