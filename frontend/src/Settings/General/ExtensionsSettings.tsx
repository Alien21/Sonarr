import React from 'react';
import FieldSet from 'Components/FieldSet';
import FormInput from 'Components/Form/FormInput';
import FormInputHelpText from 'Components/Form/FormInputHelpText';
import FormLabel from 'Components/Form/FormLabel';
import FormRow from 'Components/Form/FormRow';
import { inputTypes, sizes } from 'Helpers/Props';
import { InputChanged } from 'typings/inputs';
import { PendingSection } from 'typings/pending';
import translate from 'Utilities/String/translate';
import { GeneralSettingsModel } from './useGeneralSettings';

interface ExtensionsSettingsProps {
  settings: PendingSection<GeneralSettingsModel>;
  onInputChange: (change: InputChanged) => void;
}
const extensionOptions = [
  {
    name: 'preferDualAudio',
    label: 'PreferDualAudio',
    helpText: 'PreferDualAudioHelpText',
  },
  {
    name: 'allowAutomaticImport',
    label: 'AllowAutomaticImport',
    helpText: 'AllowAutomaticImportHelpText',
  },
  {
    name: 'parseTvdbIdFromReleaseName',
    label: 'ParseTvdbIdFromReleaseName',
    helpText: 'ParseTvdbIdFromReleaseNameHelpText',
  },
  {
    name: 'parseEpisodeNumberOnlyAsSeasonOne',
    label: 'ParseEpisodeNumberOnlyAsSeasonOne',
    helpText: 'ParseEpisodeNumberOnlyAsSeasonOneHelpText',
  },
  {
    name: 'blockAutoImportForExistingEpisodeFiles',
    label: 'PreserveDownloadsForExistingEpisodes',
    helpText: 'PreserveDownloadsForExistingEpisodesHelpText',
  },
  {
    name: 'analyzeCompletedDownloadFiles',
    label: 'AnalyzeCompletedDownloadFiles',
    helpText: 'AnalyzeCompletedDownloadFilesHelpText',
  },
  {
    name: 'useSeriesInfoLanguage',
    label: 'UseSeriesInfoLanguage',
    helpText: 'UseSeriesInfoLanguageHelpText',
  },
] as const;

function ExtensionsSettings({
  settings,
  onInputChange,
}: ExtensionsSettingsProps) {
  return (
    <FieldSet legend={translate('Extensions')}>
      {extensionOptions.map(({ name, label, helpText }) => (
        <FormRow key={name} size={sizes.MEDIUM}>
          <FormLabel>{translate(label)}</FormLabel>
          <FormInputHelpText text={translate(helpText)} />
          <FormInput
            type={inputTypes.CHECK}
            name={name}
            onChange={onInputChange}
            {...settings[name]}
          />
        </FormRow>
      ))}
      <FormRow size={sizes.MEDIUM}>
        <FormLabel>{translate('TheTvdbApiKey')}</FormLabel>
        <FormInputHelpText text={translate('TheTvdbApiKeyHelpText')} />
        <FormInput
          type={inputTypes.PASSWORD}
          name="theTvdbApiKey"
          readOnly={!settings.useSeriesInfoLanguage.value}
          onChange={onInputChange}
          {...settings.theTvdbApiKey}
        />
      </FormRow>
    </FieldSet>
  );
}

export default ExtensionsSettings;
