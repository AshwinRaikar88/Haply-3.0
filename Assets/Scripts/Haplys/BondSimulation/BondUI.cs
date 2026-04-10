// Attach to a UI Canvas TextMeshPro text object
using TMPro;
using UnityEngine;

namespace HaplyBond
{
    public class BondUI : MonoBehaviour
    {
        public BondSimulation sim;
        private TMP_Text _label;

        private void Awake() => _label = GetComponent<TMP_Text>();

        private void Update()
        {
            var (state, stretch) = sim.GetBondInfo();

            string stateStr = state switch
            {
                BondSimulation.BondState.Bonded  => $"<color=green>BONDED</color>  tension {stretch:P0}",
                BondSimulation.BondState.Snapped => "<color=red>SNAPPED</color>",
                _                                => "unbound — bring cursors close"
            };

            _label.text = $"Bond: {stateStr}";
        }
    }
}