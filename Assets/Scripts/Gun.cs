using UnityEngine;
using UnityEngine.InputSystem;

public class Gun : MonoBehaviour
{
    [Header("Gun Settings")]
    public float damage = 25f;
    public float range = 100f;
    public float fireRate = 10f;

    [Header("References")]
    public Camera fpsCam;
    public ParticleSystem muzzleFlash;
    public AudioSource gunSound;

    float nextTimeToFire;

    // Called by Input System event
    public void OnAttack(InputAction.CallbackContext ctx)
    {
        if (!ctx.performed) return;
        Shoot();
    }


    void Start()
    {
        if (fpsCam == null)
            fpsCam = GetComponentInParent<Camera>();
    }

    void Shoot()
    {
        if (muzzleFlash) muzzleFlash.Play();
        if (gunSound) gunSound.Play();

        Debug.Log("Gun: Shoot called");

        if (fpsCam == null)
        {
            Debug.LogWarning("Gun: fpsCam is null - cannot perform raycast");
            return;
        }

        if (Physics.Raycast(fpsCam.transform.position, fpsCam.transform.forward, out RaycastHit hit, range))
        {
            Debug.Log($"Gun: Hit '{hit.transform.name}' at {hit.point} (distance {hit.distance:F2})");

            var target = hit.transform.GetComponent<Target>();
            if (target != null)
            {
                target.TakeDamage(damage);
                Debug.Log($"Gun: Applied {damage} damage to '{hit.transform.name}'");
            }
            else
            {
                Debug.Log("Gun: Hit object has no Target component");
            }
        }
        else
        {
            Debug.Log("Gun: Shot missed");
        }
        
    }
}
