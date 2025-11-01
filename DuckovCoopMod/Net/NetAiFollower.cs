// Escape-From-Duckov-Coop-Mod-Preview
// Copyright (C) 2025 Mr.sans and InitLoader's team
//
// This program is not a free software.
// It's distributed under a license based on AGPL-3.0,
// with strict additional restrictions:
// YOU MUST NOT use this software for commercial purposes.
// YOU MUST NOT use this software to run a headless game server.
// YOU MUST include a conspicuous notice of attribution to
// Mr-sans-and-InitLoader-s-team/Escape-From-Duckov-Coop-Mod-Preview as the original author.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU Affero General Public License for more details.

using UnityEngine;

namespace DuckovCoopMod
{
    /// <summary>
    /// Mirrors remote AI movement and animation locally.
    /// - Resolves CharacterMainControl and model/anim components after shell swaps.
    /// - Smooths toward target parameters received from the network (speed/dir/hand/gunReady/dashing).
    /// - Rebinds animator when models change to keep references valid.
    /// </summary>
    public sealed class NetAiFollower : MonoBehaviour
    {
        Vector3 _pos, _dir;

        CharacterMainControl _cmc;
        CharacterModel _model;

        CharacterAnimationControl _animctl;
        CharacterAnimationControl_MagicBlend _magic;
        Animator _anim;

        // MagicBlend 
        static readonly int hMoveSpeed = Animator.StringToHash("MoveSpeed");
        static readonly int hMoveDirX = Animator.StringToHash("MoveDirX");
        static readonly int hMoveDirY = Animator.StringToHash("MoveDirY");
        static readonly int hHandState = Animator.StringToHash("HandState");
        static readonly int hGunReady = Animator.StringToHash("GunReady");
        static readonly int hDashing = Animator.StringToHash("Dashing");

        // 
        float _tSpeed, _tDirX, _tDirY;
        int _tHand;
        bool _tGunReady, _tDashing;

        // 
        float _cSpeed, _cDirX, _cDirY;
        int _cHand;
        bool _cGunReady, _cDashing;



        void Awake()
        {
            _cmc = GetComponentInParent<CharacterMainControl>(true);

            if (ModBehaviour.Instance && !ModBehaviour.Instance.IsRealAI(_cmc))
            {
                Destroy(this);
                return;
            }

            HookModel(_cmc ? _cmc.characterModel : null);
            TryResolveAnimator(forceRebind: true);
        }

        void OnEnable()
        {
            // / Animator
            TryResolveAnimator(forceRebind: true);
        }


        void OnDestroy()
        {
            UnhookModel();
        }

        void HookModel(CharacterModel m)
        {
            UnhookModel();
            _model = m;
            if (_model != null)
            {
                // CharacterModel OnCharacterSetEvent
                // Animator / MagicBlend
                try { _model.OnCharacterSetEvent += OnModelSet; } catch { }
            }
        }

        void UnhookModel()
        {
            if (_model != null)
            {
                try { _model.OnCharacterSetEvent -= OnModelSet; } catch { }
            }
            _model = null;
        }

        void OnModelSet()
        {
            // cmc.characterModel SetCharacterModel 
            HookModel(_cmc ? _cmc.characterModel : null);
            TryResolveAnimator(forceRebind: true);
        }

        public void ForceRebindAfterModelSwap()
        {
            HookModel(_cmc ? _cmc.characterModel : null);
            TryResolveAnimator(forceRebind: true);
        }

        void TryResolveAnimator(bool forceRebind = false)
        {
            _magic = null;
            _anim = null;
            _animctl = null;

            // 1) MagicBlend / CharacterAnimationControl animator
            var model = _cmc ? _cmc.characterModel : null;
            if (model != null)
            {
                try
                {
                    // MagicBlend/CharAnimCtrl characterModel 
                    var magics = model.GetComponentsInChildren<CharacterAnimationControl_MagicBlend>(true);
                    foreach (var m in magics)
                    {
                        if (!m) continue;
                        if ((m.characterModel == null || m.characterModel == model) && m.animator)
                        {
                            if (m.animator.isActiveAndEnabled && m.animator.gameObject.activeInHierarchy)
                            {
                                _magic = m;
                                _anim = m.animator;
                                break;
                            }
                        }
                    }

                    if (_anim == null)
                    {
                        var ctrls = model.GetComponentsInChildren<CharacterAnimationControl>(true);
                        foreach (var c in ctrls)
                        {
                            if (!c) continue;
                            if ((c.characterModel == null || c.characterModel == model) && c.animator)
                            {
                                if (c.animator.isActiveAndEnabled && c.animator.gameObject.activeInHierarchy)
                                {
                                    _animctl = c;
                                    _anim = c.animator;
                                    break;
                                }
                            }
                        }
                    }

                    // Animator 
                    if (_anim == null)
                    {
                        var anims = model.GetComponentsInChildren<Animator>(true);
                        foreach (var a in anims)
                        {
                            if (!a) continue;
                            if (a.isActiveAndEnabled && a.gameObject.activeInHierarchy)
                            {
                                _anim = a;
                                break;
                            }
                        }
                    }
                }
                catch { }
            }

            // model MagicBlend/CharAnimCtrl animator
            if (_anim == null)
            {
                try
                {
                    var m = GetComponentInChildren<CharacterAnimationControl_MagicBlend>(true);
                    if (m && m.animator && m.animator.isActiveAndEnabled && m.animator.gameObject.activeInHierarchy)
                    {
                        _magic = m;
                        _anim = m.animator;
                    }
                }
                catch { }

                if (_anim == null)
                {
                    try
                    {
                        var c = GetComponentInChildren<CharacterAnimationControl>(true);
                        if (c && c.animator && c.animator.isActiveAndEnabled && c.animator.gameObject.activeInHierarchy)
                        {
                            _animctl = c;
                            _anim = c.animator;
                        }
                    }
                    catch { }
                }
            }

            // Animator
            if (_anim == null)
            {
                try
                {
                    var anims = GetComponentsInChildren<Animator>(true);
                    foreach (var a in anims)
                    {
                        if (!a) continue;
                        if (a.isActiveAndEnabled && a.gameObject.activeInHierarchy)
                        {
                            _anim = a;
                            break;
                        }
                    }
                }
                catch { }
            }

            // & Rebind lol
            if (_anim != null)
            {
                _anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _anim.updateMode = AnimatorUpdateMode.Normal;
                _anim.applyRootMotion = false; // 
                if (forceRebind)
                {
                    try { _anim.Rebind(); _anim.Update(0f); } catch { }
                }
            }
        }


        // / 
        public void SetTarget(Vector3 pos, Vector3 dir)
        {
            _pos = pos;
            _dir = dir;
        }

        void Update()
        {
            if (_cmc == null) return;

            // Animator / 
            if (_anim == null || !_anim.isActiveAndEnabled || !_anim.gameObject.activeInHierarchy)
            {
                TryResolveAnimator(forceRebind: true);
                if (_anim == null || !_anim.isActiveAndEnabled || !_anim.gameObject.activeInHierarchy) return;
            }


            // / cmc.modelRoot 
            var t = transform;
            t.position = Vector3.Lerp(t.position, _pos, Time.deltaTime * 20f);

            var rotS = Quaternion.LookRotation(_dir, Vector3.up);
            if (_cmc.modelRoot) _cmc.modelRoot.rotation = rotS;
            t.rotation = rotS;

            // 
            float lerp = 15f * Time.deltaTime; // ~15Hz 
            _cSpeed = Mathf.Lerp(_cSpeed, _tSpeed, lerp);
            _cDirX = Mathf.Lerp(_cDirX, _tDirX, lerp);
            _cDirY = Mathf.Lerp(_cDirY, _tDirY, lerp);

            _cHand = _tHand;
            _cGunReady = _tGunReady;
            _cDashing = _tDashing;

            ApplyNow();
        }

        public void SetAnim(float speed, float dirX, float dirY, int hand, bool gunReady, bool dashing)
        {
            _tSpeed = speed;
            _tDirX = dirX;
            _tDirY = dirY;
            _tHand = hand;
            _tGunReady = gunReady;
            _tDashing = dashing;

            // 
            if (_anim && _cHand == 0 && _cSpeed == 0f && _cDirX == 0f && _cDirY == 0f)
            {
                _cSpeed = _tSpeed; _cDirX = _tDirX; _cDirY = _tDirY;
                _cHand = _tHand; _cGunReady = _tGunReady; _cDashing = _tDashing;
                ApplyNow();
            }
        }

        void ApplyNow()
        {
            if (!_anim) return;
            _anim.SetFloat(hMoveSpeed, _cSpeed);
            _anim.SetFloat(hMoveDirX, _cDirX);
            _anim.SetFloat(hMoveDirY, _cDirY);
            _anim.SetInteger(hHandState, _cHand);
            _anim.SetBool(hGunReady, _cGunReady);
            _anim.SetBool(hDashing, _cDashing);
        }

        // :InitLoader
        public void PlayAttack()
        {
            if (_magic != null) _magic.OnAttack();
            if (_animctl != null) _animctl.OnAttack();
        }









    }

}
