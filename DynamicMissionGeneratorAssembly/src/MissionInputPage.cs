﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DynamicMissionGeneratorAssembly
{
	public class MissionInputPage : MonoBehaviour
	{
		public KMSelectable RunButtonSelectable;
		public InputField InputField;
		public KMGameCommands GameCommands;
		public ModuleListItem ModuleListItemPrefab;
		public RectTransform ModuleList;
		public Scrollbar Scrollbar;
		public RectTransform ScrollView;
		private readonly List<GameObject> listItems = new List<GameObject>();
		private bool factoryEnabled;

		public KMAudio Audio;
		public KMGameInfo GameInfo;

		private readonly List<ModuleData> moduleData = new List<ModuleData>();
		private static readonly Regex tokenRegex = new Regex(@"
			\G(?:^\s*|\s+)()(?:  # Group 1 marks the position after whitespace.
				(?:(?<Hr>\d{1,9}):)?(?<Min>\d{1,9}):(?<Sec>\d{1,9})|  # Bomb time
				(?<Strikes>\d{1,9})X\b|  # Strike limit
				(?<Setting>strikes|needyactivationtime|widgets|nopacing|frontonly|factory)(?:\:(?<Value>\S*))?|  # Setting
				(?:(?<Count>\d{1,9})[;*])?  # Module pool count
				(?<ID>(?:[^\s""]|""[^""]*(?:""|$))+)  # Module IDs
			)?(?!\S)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);
		private readonly Dictionary<string, Profile> profiles = new Dictionary<string, Profile>();

		private int tabListIndex = -1;
		private int tabCursorPosition = -1;
		private string tabStub;
		private bool tabProcessing;
		private int repositionScrollView;
		private Vector2 oldMousePosition;
		private float hoverDelay;

		private static readonly ModuleData[] factoryModeList = new[]
		{
			new ModuleData("static", "Factory: Static"),
			new ModuleData("finite", "Factory: Finite"),
			new ModuleData("finitegtime", "Factory: Finite + global time"),
			new ModuleData("finitegstrikes", "Factory: Finite + global strikes"),
			new ModuleData("finitegtimestrikes", "Factory: Finite + global time and strikes"),
			new ModuleData("infinite", "Factory: Infinite"),
			new ModuleData("infinitegtime", "Factory: Infinite + global time"),
			new ModuleData("infinitegstrikes", "Factory: Infinite + global strikes"),
			new ModuleData("infinitegtimestrikes", "Factory: Infinite + global time and strikes")
		};

		private static FieldInfo cursorVertsField = typeof(InputField).GetField("m_CursorVerts", BindingFlags.NonPublic | BindingFlags.Instance);

		public void Start()
		{
			RunButtonSelectable.OnInteract += RunInteract;
			_elevatorRoomType = ReflectionHelper.FindType("ElevatorRoom");
			_gameplayStateType = ReflectionHelper.FindType("GameplayState");
			if (_gameplayStateType != null)
				_gameplayroomPrefabOverrideField = _gameplayStateType.GetField("GameplayRoomPrefabOverride", BindingFlags.Public | BindingFlags.Static);

			// KMModSettings is not used here because this isn't strictly a configuration option.
			string path = Path.Combine(Application.persistentDataPath, "LastDynamicMission.txt");
			if (File.Exists(path)) InputField.text = File.ReadAllText(path);
		}

		public void OnEnable()
		{
			InitModules();
			LoadProfiles();
			factoryEnabled = GameObject.Find("FactoryService(Clone)") != null;
		}

		public void Update()
		{
			if (EventSystem.current.currentSelectedGameObject == InputField.gameObject)
			{
				if ((Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
					RunInteract();
				if (Input.GetKeyDown(KeyCode.Tab) && listItems.Count > 0)
				{
					tabProcessing = true;
					if (tabListIndex >= 0 && tabListIndex < listItems.Count)
						SetNormalColour(listItems[tabListIndex].GetComponent<Button>(), tabListIndex % 2 == 0 ? Color.white : new Color(0.875f, 0.875f, 0.875f));
					if (listItems.Count == 0 || string.IsNullOrEmpty(listItems[0].GetComponent<ModuleListItem>().ID))
					{
						tabProcessing = false;
						return;
					}
					if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
					{
						if (tabListIndex < 0) tabListIndex = listItems.Count;
						--tabListIndex;
					} else
					{
						++tabListIndex;
						if (tabListIndex >= listItems.Count) tabListIndex = -1;
					}
					if (tabListIndex < 0)
					{
						tabCursorPosition = ReplaceToken(tabStub, false);
						Scrollbar.value = 1;
					}
					else
					{
						SetNormalColour(listItems[tabListIndex].GetComponent<Button>(), new Color(1, 0.75f, 1));
						string id = listItems[tabListIndex].GetComponent<ModuleListItem>().ID;
						tabCursorPosition = ReplaceToken(id, false);
						float offset = (-((RectTransform) ModuleList.parent).rect.height + ((RectTransform) ModuleListItemPrefab.transform).sizeDelta.y * (tabListIndex * 2 + 1)) / 2;
						float limit = ModuleList.rect.height - ((RectTransform) ModuleList.parent).rect.height;
						Scrollbar.value = Math.Min(1, 1 - offset / limit);
					}
				}

				var mousePosition = (Vector2) Input.mousePosition;
				if (mousePosition != oldMousePosition)
				{
					oldMousePosition = mousePosition;
					hoverDelay = 0.5f;
					InputField.transform.Find("Tooltip").gameObject.SetActive(false);
				}

				if (hoverDelay > 0)
				{
					hoverDelay -= Time.deltaTime;
					if (hoverDelay <= 0)
					{
						hoverDelay = 0;
						var rectTransform = (RectTransform) InputField.transform;
						if (RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, mousePosition, Camera.current, out var position2) &&
							Math.Abs(position2.x) < rectTransform.rect.width / 2 && Math.Abs(position2.y) < rectTransform.rect.height / 2)
						{
							// Get the hovered word.
							var charPosition = InputField.GetCharacterIndexFromPosition(position2);
							if (charPosition >= InputField.text.Length || InputField.text[charPosition] == ',' || InputField.text[charPosition] == '+') return;
							var matches = tokenRegex.Matches(InputField.text);
							foreach (Match match in matches)
							{
								if (charPosition < match.Index) break;
								if (charPosition <= match.Index + match.Length)
								{
									var group = match.Groups["ID"];
									if (group.Success)
									{
										if (charPosition < group.Index || charPosition >= group.Index + group.Length) break;
										var start = group.Value.LastIndexOfAny(new[] { ',', '+' }, charPosition - group.Index) + 1;
										var end = group.Value.IndexOfAny(new[] { ',', '+' }, charPosition - group.Index);
										if (end < 0) end = group.Length;
										var id = group.Value.Substring(start, end - start).Replace("\"", "");
										InputField.transform.Find("Tooltip").GetComponent<ModuleListItem>().ID = id;
										ShowPopup((RectTransform) InputField.transform.Find("Tooltip"), position2, position2);
										InputField.transform.Find("Tooltip").gameObject.SetActive(true);

										var entry = moduleData.FirstOrDefault(e => e.ModuleType == id);
										if (entry != null)
										{
											InputField.transform.Find("Tooltip").GetComponent<ModuleListItem>().Name = entry.DisplayName;
										}
										else
											InputField.transform.Find("Tooltip").GetComponent<ModuleListItem>().Name = "";
									}
								}
							}
						}
					}
				}
			}
		}

		public void LateUpdate()
		{
			if (repositionScrollView > 0)
			{
				--repositionScrollView;
				if (repositionScrollView == 0)
				{
					ScrollView.gameObject.SetActive(true);
					var array = (UIVertex[]) cursorVertsField.GetValue(InputField);
					ShowPopup(ScrollView, array[0].position, array[3].position);
				}
			}
		}

		private void ShowPopup(RectTransform popup, Vector3 cursorBottom, Vector3 cursorTop)
		{
			var inputFieldTransform = (RectTransform) InputField.transform;
			popup.pivot = new Vector2(0, 1);
			popup.localPosition = cursorBottom + new Vector3(0, 4);
			if (-popup.localPosition.y + popup.sizeDelta.y > inputFieldTransform.rect.height / 2)
			{
				popup.pivot = Vector2.zero;
				popup.localPosition = cursorTop - new Vector3(0, 4);
			}
			float d = popup.localPosition.x + popup.sizeDelta.x - inputFieldTransform.rect.width / 2;
			if (d > 0)
			{
				popup.localPosition -= new Vector3(d, 0);
			}
		}

		private static void SetNormalColour(Selectable selectable, Color color)
		{
			var colours = selectable.colors;
			colours.normalColor = color;
			selectable.colors = colours;
		}

		private bool RunInteract()
		{
			if (InputField == null)
				return false;
			if (string.IsNullOrEmpty(InputField.text))
			{
				Audio.PlayGameSoundAtTransform(KMSoundOverride.SoundEffect.Strike, transform);
				return false;
			}

			bool success = ParseTextToMission(InputField.text, out KMMission mission, out var messages);
			if (!success)
			{
				Audio.PlayGameSoundAtTransform(KMSoundOverride.SoundEffect.Strike, transform);
				foreach (var item in listItems) Destroy(item);
				listItems.Clear();
				foreach (string m in messages)
				{
					var item = Instantiate(ModuleListItemPrefab, ModuleList);
					item.Name = m;
					item.ID = "";
					listItems.Add(item.gameObject);
				}
				return false;
			}

			try
			{
				File.WriteAllText(Path.Combine(Application.persistentDataPath, "LastDynamicMission.txt"), InputField.text);
			}
			catch (Exception ex)
			{
				Debug.LogError("[Dynamic Mission Generator] Could not write LastDynamicMission.txt");
				Debug.LogException(ex, this);
			}
			GameCommands.StartMission(mission, "-1");

			return false;
		}

		public void TextChanged(string newText)
		{
			if (tabProcessing) return;
			tabListIndex = -1;
			tabCursorPosition = InputField.caretPosition;

			if (InputField.caretPosition >= 2 && newText[InputField.caretPosition - 1] == ',' && newText[InputField.caretPosition - 2] == ' ')
			{
				// If a comma was typed immediately after an auto-inserted space, remove the space.
				StartCoroutine(SetSelectionCoroutine(InputField.caretPosition - 1));
				InputField.text = newText.Remove(InputField.caretPosition - 2, 1);
				return;
			}

			foreach (var item in listItems) Destroy(item);
			listItems.Clear();

			var matches = tokenRegex.Matches(newText.Substring(0, InputField.caretPosition));
			if (matches.Count > 0)
			{
				var lastMatch = matches[matches.Count - 1];
				if (lastMatch.Groups["Min"].Success)
				{
					string text = $"Time: {(lastMatch.Groups["Hr"].Success ? int.Parse(lastMatch.Groups["Hr"].Value) + "h " : "")}{int.Parse(lastMatch.Groups["Min"].Value)}m {int.Parse(lastMatch.Groups["Sec"].Value)}s";
					var item = AddListItem("", text, false);
					item.HighlightName(6, text.Length - 6);
				}
				else if (lastMatch.Groups["Strikes"].Success)
				{
					var item = AddListItem("", "Strike limit: " + lastMatch.Value.TrimStart(), false);
					item.HighlightName(14, item.Name.Length - 14);
				}
				else if (lastMatch.Groups["Setting"].Success)
				{
					if (lastMatch.Groups["Setting"].Value.Equals("factory", StringComparison.InvariantCultureIgnoreCase))
					{
						if (factoryEnabled)
						{
							foreach (var m in factoryModeList)
							{
								if (m.ModuleType.StartsWith(lastMatch.Groups["Value"].Value, StringComparison.InvariantCultureIgnoreCase))
								{
									var item = AddListItem("factory:" + m.ModuleType, m.DisplayName, true);
									item.HighlightID(0, lastMatch.Groups["Value"].Length + 8);
								}
							}
						}
						else
						{
							var item = AddListItem(lastMatch.Groups["Setting"].Value + ":" + lastMatch.Groups["Value"].Value, "[Factory is not enabled]", false);
							item.HighlightID(0, item.ID.Length);
						}
					}
					else
					{
						var item = AddListItem("", lastMatch.Groups["Setting"].Value + ": " + lastMatch.Groups["Value"].Value, false);
						item.HighlightName(lastMatch.Groups["Setting"].Value.Length + 2, item.Name.Length - (lastMatch.Groups["Setting"].Value.Length + 2));
					}
				}
				else if (lastMatch.Groups["ID"].Success)
				{
					string s = GetLastModuleID(lastMatch.Groups["ID"].Value).Replace("\"", "");
					tabStub = s;
					if (!lastMatch.Groups["Count"].Success && !string.IsNullOrEmpty(lastMatch.Groups["ID"].Value) && lastMatch.Groups["ID"].Value.All(char.IsDigit))
					{
						var item = AddListItem($"{s}:00", "[Set time]", true);
						item.HighlightID(0, s.Length);
						item = AddListItem($"{s}X", "[Set strike limit]", true);
						item.HighlightID(0, s.Length);
						item = AddListItem($"{s}*", "[Set module pool count]", true);
						item.HighlightID(0, s.Length);
					}
					foreach (var m in moduleData)
					{
						bool id = m.ModuleType.StartsWith(s, StringComparison.InvariantCultureIgnoreCase);
						bool name = !id && m.DisplayName.StartsWith(s, StringComparison.InvariantCultureIgnoreCase);
						if (id || name)
						{
							var item = AddListItem(m.ModuleType, m.DisplayName, true);
							if (id) item.HighlightID(0, s.Length);
							else if (name) item.HighlightName(0, s.Length);
						}
					}
				}
			}

			if (listItems.Count > 0)
			{
				repositionScrollView = 2;
			}
			else
			{
				repositionScrollView = 0;
				ScrollView.gameObject.SetActive(false);
			}
		}

		private static string GetLastModuleID(string list) => list.Substring(GetLastModuleIDPos(list));
		private static int GetLastModuleIDPos(string list) => list.LastIndexOfAny(new[] { ',', '+' }) + 1;

		private ModuleListItem AddListItem(string id, string text, bool addClickEvent)
		{
			var item = Instantiate(ModuleListItemPrefab, ModuleList);
			if (listItems.Count % 2 != 0) SetNormalColour(item.GetComponent<Button>(), new Color(0.875f, 0.875f, 0.875f));
			if (addClickEvent) item.Click += ModuleListItem_Click;
			item.Name = text;
			item.ID = id;
			listItems.Add(item.gameObject);
			return item;
		}

		private void ModuleListItem_Click(object sender, EventArgs e)
		{
			string id = ((ModuleListItem) sender).ID;
			tabProcessing = true;
			ReplaceToken(id, !id.EndsWith("*"));
			tabProcessing = false;
		}

		private int ReplaceToken(string id, bool space)
		{
			var match = tokenRegex.Matches(InputField.text.Substring(0, tabCursorPosition)).Cast<Match>().Last();

			int startIndex;
			if (match.Groups["ID"].Success)
			{
				startIndex = match.Groups["ID"].Index + GetLastModuleIDPos(match.Groups["ID"].Value);
				if (id.Contains(' ') && match.Groups["ID"].Value.Take(startIndex).Count(c => c == '"') % 2 == 0)
					id = "\"" + id + "\"";
				if (space) id += " ";
			}
			else startIndex = match.Groups[1].Index;
			InputField.text = InputField.text.Remove(startIndex, tabCursorPosition - startIndex).Insert(startIndex, id);
			InputField.Select();
			StartCoroutine(SetSelectionCoroutine(startIndex + id.Length));
			return startIndex + id.Length;
		}

		private IEnumerator SetSelectionCoroutine(int pos)
		{
			yield return null;
			InputField.caretPosition = pos;
			InputField.ForceLabelUpdate();
			if (tabProcessing) tabProcessing = false;
			else TextChanged(InputField.text);
		}

		private void InitModules()
		{
			moduleData.Clear();
			moduleData.Add(new ModuleData("ALL_SOLVABLE", "[All solvable modules]"));
			moduleData.Add(new ModuleData("ALL_NEEDY", "[All needy modules]"));
			moduleData.Add(new ModuleData("ALL_VANILLA", "[All vanilla solvable modules]"));
			moduleData.Add(new ModuleData("ALL_MODS", "[All mod solvable modules]"));
			moduleData.Add(new ModuleData("ALL_VANILLA_NEEDY", "[All vanilla needy modules]"));
			moduleData.Add(new ModuleData("ALL_MODS_NEEDY", "[All mod needy modules]"));
			moduleData.Add(new ModuleData("frontonly", "[Front face only]"));
			moduleData.Add(new ModuleData("nopacing", "[Disable pacing events]"));
			moduleData.Add(new ModuleData("widgets:", "[Set widget count]"));
			moduleData.Add(new ModuleData("needyactivationtime:", "[Set needy activation time in seconds]"));
			if (factoryEnabled) moduleData.Add(new ModuleData("factory:", "[Set Factory mode]"));
			moduleData.Add(new ModuleData("Wires", "Wires"));
			moduleData.Add(new ModuleData("Keypad", "Keypad"));
			moduleData.Add(new ModuleData("Memory", "Memory"));
			moduleData.Add(new ModuleData("Maze", "Maze"));
			moduleData.Add(new ModuleData("Password", "Password"));
			moduleData.Add(new ModuleData("BigButton", "The Button"));
			moduleData.Add(new ModuleData("Simon", "Simon Says"));
			moduleData.Add(new ModuleData("WhosOnFirst", "Who's On First"));
			moduleData.Add(new ModuleData("Morse", "Morse Code"));
			moduleData.Add(new ModuleData("Venn", "Complicated Wires"));
			moduleData.Add(new ModuleData("WireSequence", "Wire Sequence"));
			moduleData.Add(new ModuleData("NeedyVentGas", "Venting Gas"));
			moduleData.Add(new ModuleData("NeedyCapacitor", "Capacitor Discharge"));
			moduleData.Add(new ModuleData("NeedyKnob", "Knob"));

			if (Application.isEditor)
			{
				moduleData.Add(new ModuleData($"Space Test", $"Space Test"));
				for (int i = 0; i < 30; ++i)
				{
					moduleData.Add(new ModuleData($"ScrollTest{i:00}", $"Scroll Test {i}"));
				}
			}

			if (DynamicMissionGenerator.ModSelectorApi != null)
			{
				var assembly = DynamicMissionGenerator.ModSelectorApi.GetType().Assembly;
				var serviceType = assembly.GetType("ModSelectorService");
				object service = serviceType.GetProperty("Instance").GetValue(null, null);
				var allSolvableModules = (IDictionary) serviceType.GetField("_allSolvableModules", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(service);
				var allNeedyModules = (IDictionary) serviceType.GetField("_allNeedyModules", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(service);

				foreach (object entry in allSolvableModules.Cast<object>().Concat(allNeedyModules.Cast<object>()))
				{
					string id = (string) entry.GetType().GetProperty("Key").GetValue(entry, null);
					object value = entry.GetType().GetProperty("Value").GetValue(entry, null);
					string name = (string) value.GetType().GetProperty("ModuleName").GetValue(value, null);
					moduleData.Add(new ModuleData(id, name));
				}
			}

			moduleData.Sort((a, b) => a.ModuleType.CompareTo(b.ModuleType));
		}

		private void LoadProfiles()
		{
			profiles.Clear();
			string path = Path.Combine(Application.persistentDataPath, "ModProfiles");
			if (!Directory.Exists(path)) return;

			var allSolvableModules = new HashSet<string>((IEnumerable<string>) DynamicMissionGenerator.ModSelectorApi["AllSolvableModules"]);
			var allNeedyModules = new HashSet<string>((IEnumerable<string>) DynamicMissionGenerator.ModSelectorApi["AllNeedyModules"]);

			try
			{
				foreach (string file in Directory.GetFiles(path, "*.json"))
				{
					try
					{
						using var reader = new StreamReader(file);
						var profile = new JsonSerializer().Deserialize<Profile>(new JsonTextReader(reader));
						if (profile.DisabledList == null)
						{
							Debug.LogWarning($"[Profile Revealer] Could not load profile {Path.GetFileName(file)}");
							continue;
						}

						string profileName = Path.GetFileNameWithoutExtension(file);
						profiles.Add(profileName, profile);
						// Don't list defuser profiles that disable no modules as completion options.
						bool any = false;
						if (profile.Operation == ProfileType.Expert || profile.DisabledList.Where(m => allSolvableModules.Contains(m)).Any())
						{
							any = true;
							moduleData.Add(new ModuleData("profile:" + profileName, profileName + " (solvable modules enabled by profile)"));
						}
						if (profile.Operation == ProfileType.Expert || profile.DisabledList.Where(m => allNeedyModules.Contains(m)).Any())
						{
							any = true;
							moduleData.Add(new ModuleData("needyprofile:" + profileName, profileName + " (needy modules enabled by profile)"));
						}
						if (!any)
						{
							Debug.Log($"[Dynamic Mission Generator] Not listing {profileName} as it is a defuser profile that seems to disable no modules.");
						}
					}
					catch (Exception ex)
					{
						Debug.LogWarning($"[Dynamic Mission Generator] Could not load profile {Path.GetFileName(file)}");
						Debug.LogException(ex, this);
					}
				}
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[Dynamic Mission Generator] Could not load profiles");
				Debug.LogException(ex, this);
			}
		}

		private bool ParseTextToMission(string text, out KMMission mission, out List<string> messages)
		{
			messages = new List<string>();

			var matches = tokenRegex.Matches(text);
			if (matches.Count == 0 || (matches.Count == 1 && !matches[0].Value.Any(c => !char.IsWhiteSpace(c))) || matches[matches.Count - 1].Index + matches[matches.Count - 1].Length < text.Length)
			{
				messages.Add("Syntax error");
				mission = null;
				return false;
			}

			var allSolvableModules = new HashSet<string>((IEnumerable<string>) DynamicMissionGenerator.ModSelectorApi["AllSolvableModules"]);
			var allNeedyModules = new HashSet<string>((IEnumerable<string>) DynamicMissionGenerator.ModSelectorApi["AllNeedyModules"]);
			var enabledSolvableModules = new HashSet<string>(allSolvableModules.Except((IEnumerable<string>) DynamicMissionGenerator.ModSelectorApi["DisabledSolvableModules"]));
			var enabledNeedyModules = new HashSet<string>(allNeedyModules.Except((IEnumerable<string>) DynamicMissionGenerator.ModSelectorApi["DisabledNeedyModules"]));

			bool timeSpecified = false, strikesSpecified = false, anySolvableModules = false;
			int? factoryMode = null;
			mission = ScriptableObject.CreateInstance<KMMission>();
			mission.PacingEventsEnabled = true;
			mission.GeneratorSetting = new KMGeneratorSetting();
			List<KMComponentPool> pools = new List<KMComponentPool>();
			foreach (Match match in matches)
			{
				if (match.Groups["Min"].Success)
				{
					if (timeSpecified)
						messages.Add("Time specified multiple times");
					else
					{
						timeSpecified = true;
						mission.GeneratorSetting.TimeLimit = (match.Groups["Hr"].Success ? int.Parse(match.Groups["Hr"].Value) * 3600 : 0) +
							int.Parse(match.Groups["Min"].Value) * 60 + int.Parse(match.Groups["Sec"].Value);
						if (mission.GeneratorSetting.TimeLimit <= 0) messages.Add("Invalid time limit");
					}
				}
				else if (match.Groups["Strikes"].Success)
				{
					if (strikesSpecified) messages.Add("Strike limit specified multiple times");
					else
					{
						strikesSpecified = true;
						mission.GeneratorSetting.NumStrikes = int.Parse(match.Groups["Strikes"].Value);
						if (mission.GeneratorSetting.NumStrikes <= 0) messages.Add("Invalid strike limit");
					}
				}
				else if (match.Groups["Setting"].Success)
				{
					switch (match.Groups["Setting"].Value.ToLowerInvariant())
					{
						case "strikes":
							if (match.Groups["Value"].Success) {
								if (strikesSpecified) messages.Add("Strike limit specified multiple times");
								else
								{
									strikesSpecified = true;
									mission.GeneratorSetting.NumStrikes = int.Parse(match.Groups["Value"].Value);
									if (mission.GeneratorSetting.NumStrikes <= 0) messages.Add("Invalid strike limit");
								}
							}
							break;
						case "needyactivationtime":
							if (match.Groups["Value"].Success) mission.GeneratorSetting.TimeBeforeNeedyActivation = int.Parse(match.Groups["Value"].Value);
							break;
						case "widgets":
							if (match.Groups["Value"].Success) mission.GeneratorSetting.OptionalWidgetCount = int.Parse(match.Groups["Value"].Value);
							break;
						case "nopacing": mission.PacingEventsEnabled = false; break;
						case "frontonly": mission.GeneratorSetting.FrontFaceOnly = true; break;
						case "factory":
							if (factoryMode.HasValue) messages.Add("Factory mode specified multiple times");
							else if (!factoryEnabled) messages.Add("Factory does not seem to be enabled");
							else
							{
								for (factoryMode = 0; factoryMode < factoryModeList.Length; ++factoryMode)
								{
									if (factoryModeList[factoryMode.Value].ModuleType.Equals(match.Groups["Value"].Value, StringComparison.InvariantCultureIgnoreCase)) break;
								}
								if (factoryMode >= factoryModeList.Length)
								{
									messages.Add("Invalid factory mode");
								}
								else
								{
									pools.Add(new KMComponentPool()
									{
										ModTypes = new List<string>() { "Factory Mode" },
										Count = factoryMode.Value
									});
								}
							}
							break;
					}
				}
				else if (match.Groups["ID"].Success)
				{
					KMComponentPool pool = new KMComponentPool
					{
						Count = match.Groups["Count"].Success ? int.Parse(match.Groups["Count"].Value) : 1,
						ComponentTypes = new List<KMComponentPool.ComponentTypeEnum>(),
						ModTypes = new List<string>()
					};
					if (pool.Count <= 0) messages.Add("Invalid module pool count");

					bool allSolvable = true;
					string list = match.Groups["ID"].Value.Replace("\"", "").Trim();
					switch (list)
					{
						case "ALL_SOLVABLE":
							anySolvableModules = true;
							pool.AllowedSources = KMComponentPool.ComponentSource.Base | KMComponentPool.ComponentSource.Mods;
							pool.SpecialComponentType = KMComponentPool.SpecialComponentTypeEnum.ALL_SOLVABLE;
							break;
						case "ALL_NEEDY":
							pool.AllowedSources = KMComponentPool.ComponentSource.Base | KMComponentPool.ComponentSource.Mods;
							pool.SpecialComponentType = KMComponentPool.SpecialComponentTypeEnum.ALL_NEEDY;
							break;
						case "ALL_VANILLA":
							anySolvableModules = true;
							pool.AllowedSources = KMComponentPool.ComponentSource.Base;
							pool.SpecialComponentType = KMComponentPool.SpecialComponentTypeEnum.ALL_SOLVABLE;
							break;
						case "ALL_MODS":
							anySolvableModules = true;
							pool.AllowedSources = KMComponentPool.ComponentSource.Mods;
							pool.SpecialComponentType = KMComponentPool.SpecialComponentTypeEnum.ALL_SOLVABLE;
							break;
						case "ALL_VANILLA_NEEDY":
							pool.AllowedSources = KMComponentPool.ComponentSource.Base;
							pool.SpecialComponentType = KMComponentPool.SpecialComponentTypeEnum.ALL_NEEDY;
							break;
						case "ALL_MODS_NEEDY":
							pool.AllowedSources = KMComponentPool.ComponentSource.Mods;
							pool.SpecialComponentType = KMComponentPool.SpecialComponentTypeEnum.ALL_NEEDY;
							break;
						default:
							bool useProfile = list.StartsWith("profile:", StringComparison.InvariantCultureIgnoreCase);
							bool useNeedyProfile = !useProfile && list.StartsWith("needyprofile:", StringComparison.InvariantCultureIgnoreCase);

							if (useProfile || useNeedyProfile)
							{
								string profileName = list.Substring(useNeedyProfile ? 13 : 8);
								if (!profiles.TryGetValue(profileName, out var profile))
								{
									messages.Add($"No profile named '{profileName}' was found.");
								}
								else
								{
									Debug.Log("[Dynamic Mission Generator] Disabled list: " + string.Join(", ", profile.DisabledList.ToArray()));
									pool.ModTypes.AddRange((useNeedyProfile ? enabledNeedyModules : enabledSolvableModules).Except(profile.DisabledList));
									if (pool.ModTypes.Count == 0)
									{
										messages.Add($"Profile '{profileName}' enables no valid modules.");
										allSolvable = false;
									}
									else
									{
										allSolvable = useProfile;
									}
								}
							}
							else
							{
								foreach (string id in list.Split(',', '+').Select(s => s.Trim()))
								{
									switch (id)
									{
										case "WireSequence": pool.ComponentTypes.Add(KMComponentPool.ComponentTypeEnum.WireSequence); break;
										case "Wires": pool.ComponentTypes.Add(KMComponentPool.ComponentTypeEnum.Wires); break;
										case "WhosOnFirst": pool.ComponentTypes.Add(KMComponentPool.ComponentTypeEnum.WhosOnFirst); break;
										case "Simon": pool.ComponentTypes.Add(KMComponentPool.ComponentTypeEnum.Simon); break;
										case "Password": pool.ComponentTypes.Add(KMComponentPool.ComponentTypeEnum.Password); break;
										case "Morse": pool.ComponentTypes.Add(KMComponentPool.ComponentTypeEnum.Morse); break;
										case "Memory": pool.ComponentTypes.Add(KMComponentPool.ComponentTypeEnum.Memory); break;
										case "Maze": pool.ComponentTypes.Add(KMComponentPool.ComponentTypeEnum.Maze); break;
										case "Keypad": pool.ComponentTypes.Add(KMComponentPool.ComponentTypeEnum.Keypad); break;
										case "Venn": pool.ComponentTypes.Add(KMComponentPool.ComponentTypeEnum.Venn); break;
										case "BigButton": pool.ComponentTypes.Add(KMComponentPool.ComponentTypeEnum.BigButton); break;
										case "NeedyCapacitor":
											allSolvable = false;
											pool.ComponentTypes.Add(KMComponentPool.ComponentTypeEnum.NeedyCapacitor);
											break;
										case "NeedyVentGas":
											allSolvable = false;
											pool.ComponentTypes.Add(KMComponentPool.ComponentTypeEnum.NeedyVentGas);
											break;
										case "NeedyKnob":
											allSolvable = false;
											pool.ComponentTypes.Add(KMComponentPool.ComponentTypeEnum.NeedyKnob);
											break;
										default:
											if (!allSolvableModules.Contains(id) && !allNeedyModules.Contains(id))
												messages.Add($"'{id}' is an unknown module ID.");
											else if (!enabledSolvableModules.Contains(id) && !enabledNeedyModules.Contains(id))
												messages.Add($"'{id}' is disabled.");
											else
											{
												allSolvable = allSolvable && allSolvableModules.Contains(id);
												pool.ModTypes.Add(id);
											}
											break;
									}
								}
							}
							break;
					}
					if (allSolvable) anySolvableModules = true;
					if (pool.ModTypes.Count == 0)
						pool.ModTypes = null;
					if (pool.ComponentTypes.Count == 0)
						pool.ComponentTypes = null;
					pools.Add(pool);
				}
			}

			if (!anySolvableModules) messages.Add("No regular modules");
			mission.GeneratorSetting.ComponentPools.AddRange(pools);
			if (mission.GeneratorSetting.GetComponentCount() - (factoryMode ?? 0) > GetMaxModules())
				messages.Add($"Too many modules for any bomb casing ({mission.GeneratorSetting.GetComponentCount()} > {GetMaxModules()}).");

			if (messages.Count > 0)
			{
				Destroy(mission);
				mission = null;
				return false;
			}
			messages = null;
			mission.DisplayName = "Custom Freeplay";
			if (!timeSpecified) mission.GeneratorSetting.TimeLimit = mission.GeneratorSetting.GetComponentCount() * 120;
			if (!strikesSpecified) mission.GeneratorSetting.NumStrikes = Math.Max(3, mission.GeneratorSetting.GetComponentCount() / 12);
			return true;
		}

		private int GetMaxModules()
		{
			GameObject roomPrefab = (GameObject) _gameplayroomPrefabOverrideField.GetValue(null);
			if (roomPrefab == null) return GameInfo.GetMaximumBombModules();
			return roomPrefab.GetComponentInChildren(_elevatorRoomType, true) != null ? 54 : GameInfo.GetMaximumBombModules();
		}

		private static Type _gameplayStateType;
		private static FieldInfo _gameplayroomPrefabOverrideField;

		private static Type _elevatorRoomType;

		private struct Profile
		{
			public HashSet<string> DisabledList;
			public ProfileType Operation;
		}

		private enum ProfileType
		{
			Expert,
			Defuser
		}
	}
}