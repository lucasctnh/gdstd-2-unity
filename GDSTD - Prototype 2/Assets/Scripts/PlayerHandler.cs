using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using Mirror;

public class PlayerHandler : NetworkBehaviour {

	[Header("Player Objects")]
	public GameObject projectilePrefab;
	public GameObject chargedProjectilePrefab;
	public GameObject linePrefab;
	public Transform shootSpawnPoint;
	public Transform lineSpawnPoint;

	[Header("In-Game Variables")]
	[Tooltip("The speed that the player will move.")]
	public float speed = 20f;
	[Tooltip("The speed that the camera will rotate.")]
	public float rotationDamping;
	[Tooltip("The camera delay to stop rotation.")]
	public float lerpFactor = .5f;
	public float rechargeTime = .3f;
	public float timeForCharge = 1.5f;
	public float chargeRotationSpeed = 100f;

	[Header("In-Script Variables")]
	private GameManager _gameManager;
	private Rigidbody _localRigidbody;
	private float _time = 0;
	private float _chargeTime = 0;
	private bool _showLine = true;
	private GameObject _line;

	private void Start() {
		_gameManager = GameObject.Find("Game Manager").GetComponent<GameManager>();

		if (isLocalPlayer) {
			_localRigidbody = gameObject.GetComponent<Rigidbody>();

			if (isServer) _gameManager.hostClient = gameObject;
			else _gameManager.justClient = gameObject;
		} else {
			if (isServer) _gameManager.justClient = gameObject;
			else _gameManager.hostClient = gameObject;
		}
	}

	private void Update() {
		if (!isLocalPlayer && _gameManager) {
			_gameManager.otherClientPosition = transform.position;
			return;
		} else {
			if (Input.GetKeyUp(KeyCode.X)) {
				_chargeTime = 0;
				_showLine = true;
				if (_line) Destroy(_line);
			}
			if (Input.GetKey(KeyCode.X)) {
				RotateByInput();
				if (_showLine) {
					_showLine = false;
					DrawLine();
				}

				_chargeTime += Time.deltaTime;

				if (_chargeTime > timeForCharge) {
					Destroy(_line);
					if (isServer) CmdSpawnChargedProjectile("hostClient");
					else CmdSpawnChargedProjectile("justClient");

					_chargeTime = 0;
					_showLine = true;
				}
			} else {
				MoveInCircle();
				RotateTowardsTarget();
				_time += Time.deltaTime;

				if (Input.GetKeyUp(KeyCode.Z))
					_time = 0;
				if (Input.GetKey(KeyCode.Z)) {
					if (_time > rechargeTime) {
						if (isServer) CmdSpawnProjectile("hostClient");
						else CmdSpawnProjectile("justClient");

						_time = 0;
					}
				}
			}
		}
	}

	void DrawLine() {
		_line = Instantiate(linePrefab, lineSpawnPoint.position, transform.rotation);
		Vector3 eulerAngles = _line.transform.rotation.eulerAngles;
		_line.transform.rotation = Quaternion.Euler(90, eulerAngles.y, eulerAngles.z);

		if (isServer) _line.transform.SetParent(_gameManager.hostClient.GetComponent<Transform>());
		else _line.transform.SetParent(_gameManager.justClient.GetComponent<Transform>());
	}

	private void RotateByInput() {
		float horizontalInput = Input.GetAxis("Horizontal");
		Vector3 rotation = new Vector3(0f, horizontalInput * Time.deltaTime, 0f);

		transform.Rotate(rotation * chargeRotationSpeed, Space.Self);
	}

	private void MoveInCircle() {
		float verticalInput = Input.GetAxis("Vertical");
		Vector3 verticalMovement = transform.forward * verticalInput * Time.deltaTime;
		_localRigidbody.position += verticalMovement * speed;

		float horizontalInput = Input.GetAxis("Horizontal");
		Vector3 horizontalMovement = transform.right * horizontalInput * Time.deltaTime;
		_localRigidbody.position += horizontalMovement * speed;
	}

	private void RotateTowardsTarget() {
		Vector3 lookDir = _gameManager.otherClientPosition - transform.position;
		lookDir.y = 0;

		Quaternion rotation = Quaternion.LookRotation(lookDir);
		Quaternion slerpedRotation = Quaternion.Slerp(
			transform.rotation,
			rotation,
			Time.deltaTime * rotationDamping
		);

		transform.rotation = slerpedRotation;
	}

	[Command]
	private void CmdSpawnProjectile(string parent) {
		GameObject projectileClone = Instantiate(projectilePrefab,
			shootSpawnPoint.position,
			transform.rotation);
		projectileClone.GetComponent<ProjectileHandler>().spawnedBy = parent;
		NetworkServer.Spawn(projectileClone);
	}

	[Command]
	private void CmdSpawnChargedProjectile(string parent) {
		GameObject chargedClone = Instantiate(chargedProjectilePrefab,
			shootSpawnPoint.position + (transform.forward * 4.5f),
			transform.rotation);
		chargedClone.GetComponent<ChargedHandler>().spawnedBy = parent;
		NetworkServer.Spawn(chargedClone);
	}

	private void OnCollisionEnter(Collision other) {
		if (other.gameObject.CompareTag("OutBounds")) {
			NetworkManager.singleton.ServerChangeScene(SceneManager.GetActiveScene().name);
		}
	}
}
