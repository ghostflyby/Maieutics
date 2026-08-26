/**
 * Classic ipywidgets control catalog: control type -> model template.
 *
 * These are the kernel-side state templates for the controls the frontend
 * `@jupyter-widgets/jupyterlab-manager` renders. The DOM is entirely a
 * frontend concern; the kernel only needs the identity fields plus the
 * control's state defaults (props override defaults at creation).
 *
 * Only a representative subset is declared; the map is the single extension
 * point for adding controls (each entry is a model + view name plus defaults).
 */

export interface ControlTemplate {
  modelName: string;
  viewName: string;
  defaults: Record<string, unknown>;
  /** True for selection controls whose `options` prop needs label-value normalization. */
  selection?: boolean;
}

export const CONTROLS: ReadonlyMap<string, ControlTemplate> = new Map([
  [
    "IntSlider",
    {
      modelName: "IntSliderModel",
      viewName: "IntSliderView",
      defaults: {
        value: 0,
        min: 0,
        max: 100,
        step: 1,
        description: "",
        orientation: "horizontal",
        readout: true,
        readout_format: "d",
        continuous_update: true,
        disabled: false,
      },
    },
  ],
  [
    "FloatSlider",
    {
      modelName: "FloatSliderModel",
      viewName: "FloatSliderView",
      defaults: {
        value: 0,
        min: 0,
        max: 100,
        step: 0.1,
        description: "",
        orientation: "horizontal",
        readout: true,
        readout_format: ".1f",
        continuous_update: true,
        disabled: false,
      },
    },
  ],
  [
    "Button",
    {
      modelName: "ButtonModel",
      viewName: "ButtonView",
      defaults: {
        description: "",
        tooltip: "",
        disabled: false,
        button_style: "",
        icon: "",
        layout: null,
        style: null,
      },
    },
  ],
  [
    "Text",
    {
      modelName: "TextModel",
      viewName: "TextView",
      defaults: {
        value: "",
        placeholder: "",
        description: "",
        disabled: false,
        continuous_update: false,
      },
    },
  ],
  [
    "ToggleButton",
    {
      modelName: "ToggleButtonModel",
      viewName: "ToggleButtonView",
      defaults: {
        description: "",
        tooltip: "",
        value: false,
        disabled: false,
        button_style: "",
        icon: "",
      },
    },
  ],
  [
    "IntRangeSlider",
    {
      modelName: "IntRangeSliderModel",
      viewName: "IntRangeSliderView",
      defaults: {
        value: [0, 1],
        min: 0,
        max: 100,
        step: 1,
        description: "",
        orientation: "horizontal",
        readout: true,
        readout_format: "d",
        continuous_update: true,
        disabled: false,
      },
    },
  ],
  [
    "Box",
    {
      modelName: "BoxModel",
      viewName: "BoxView",
      defaults: {
        children: [],
        layout: null,
      },
    },
  ],
  [
    "VBox",
    {
      modelName: "VBoxModel",
      viewName: "VBoxView",
      defaults: {
        children: [],
        layout: null,
      },
    },
  ],
  [
    "HBox",
    {
      modelName: "HBoxModel",
      viewName: "HBoxView",
      defaults: {
        children: [],
        layout: null,
      },
    },
  ],
  [
    "Checkbox",
    {
      modelName: "CheckboxModel",
      viewName: "CheckboxView",
      defaults: {
        value: false,
        indent: true,
        description: "",
        disabled: false,
        style: null,
      },
    },
  ],
  [
    "Label",
    {
      modelName: "LabelModel",
      viewName: "LabelView",
      defaults: {
        value: "",
        placeholder: "",
        description: "",
        disabled: false,
      },
    },
  ],
  [
    "HTML",
    {
      modelName: "HTMLModel",
      viewName: "HTMLView",
      defaults: {
        value: "",
        placeholder: "",
        description: "",
        disabled: false,
      },
    },
  ],
  [
    "Textarea",
    {
      modelName: "TextareaModel",
      viewName: "TextareaView",
      defaults: {
        value: "",
        placeholder: "",
        description: "",
        disabled: false,
        rows: null,
        continuous_update: true,
      },
    },
  ],
  [
    "Password",
    {
      modelName: "PasswordModel",
      viewName: "PasswordView",
      defaults: {
        value: "",
        placeholder: "",
        description: "",
        disabled: false,
        continuous_update: false,
      },
    },
  ],
  [
    "IntText",
    {
      modelName: "IntTextModel",
      viewName: "IntTextView",
      defaults: {
        value: 0,
        step: 1,
        description: "",
        disabled: false,
        continuous_update: false,
      },
    },
  ],
  [
    "FloatText",
    {
      modelName: "FloatTextModel",
      viewName: "FloatTextView",
      defaults: {
        value: 0,
        step: null,
        description: "",
        disabled: false,
        continuous_update: false,
      },
    },
  ],
  [
    "BoundedIntText",
    {
      modelName: "BoundedIntTextModel",
      viewName: "IntTextView",
      defaults: {
        value: 0,
        min: 0,
        max: 100,
        step: 1,
        description: "",
        disabled: false,
        continuous_update: false,
      },
    },
  ],
  [
    "IntProgress",
    {
      modelName: "IntProgressModel",
      viewName: "ProgressView",
      defaults: {
        value: 0,
        min: 0,
        max: 100,
        bar_style: "",
        description: "",
        style: null,
      },
    },
  ],
  [
    "FloatProgress",
    {
      modelName: "FloatProgressModel",
      viewName: "ProgressView",
      defaults: {
        value: 0,
        min: 0,
        max: 100,
        bar_style: "",
        description: "",
        style: null,
      },
    },
  ],
  [
    "DatePicker",
    {
      modelName: "DatePickerModel",
      viewName: "DatePickerView",
      defaults: {
        value: null,
        description: "",
        disabled: false,
      },
    },
  ],
  [
    "Dropdown",
    {
      modelName: "DropdownModel",
      viewName: "DropdownView",
      defaults: {
        options: [],
        _options_labels: [],
        index: null,
        description: "",
        disabled: false,
      },
      selection: true,
    },
  ],
  [
    "Select",
    {
      modelName: "SelectModel",
      viewName: "SelectView",
      defaults: {
        options: [],
        _options_labels: [],
        index: null,
        rows: 5,
        description: "",
        disabled: false,
      },
      selection: true,
    },
  ],
  [
    "ToggleButtons",
    {
      modelName: "ToggleButtonsModel",
      viewName: "ToggleButtonsView",
      defaults: {
        options: [],
        _options_labels: [],
        index: null,
        button_style: "",
        description: "",
        disabled: false,
        style: null,
      },
      selection: true,
    },
  ],
  [
    "RadioButtons",
    {
      modelName: "RadioButtonsModel",
      viewName: "RadioButtonsView",
      defaults: {
        options: [],
        _options_labels: [],
        index: null,
        description: "",
        disabled: false,
        style: null,
      },
      selection: true,
    },
  ],
]);

/** Looks up a control template by its JSX tag name. */
export function controlTemplate(name: string): ControlTemplate | undefined {
  return CONTROLS.get(name);
}

/** True when the tag name identifies a known classic controls widget. */
export function isControl(name: string): boolean {
  return CONTROLS.has(name);
}
