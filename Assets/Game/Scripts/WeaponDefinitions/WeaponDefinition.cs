using UnityEngine;

[CreateAssetMenu(fileName = "WeaponDefinition", menuName = "Scriptable Objects/WeaponDefinition")]
public class WeaponDefinition : ScriptableObject
{
    [SerializeField] public string weaponID = "WeaponID";
    [SerializeField] public float damage = 10f;
    [SerializeField] public float range = 100f;
    [SerializeField] public float fireRate = 0.5f;
    [SerializeField] public float reloadTime = 2f;
    [SerializeField] public uint magazineSize = 30;
    [SerializeField] public GameObject viewModelPrefab;
    [SerializeField] public GameObject worldModelPrefab;
}
