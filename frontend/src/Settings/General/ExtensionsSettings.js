import PropTypes from 'prop-types';
import React from 'react';
import FieldSet from 'Components/FieldSet';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import { inputTypes, sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';

function ExtensionsSettings(props) {
  const { settings, onInputChange } = props;

  const {
    preferDualAudio,
    parseTvdbIdFromReleaseName,
    blockAutoImportForExistingEpisodeFiles,
    analyzeCompletedDownloadFiles,
    useSeriesInfoLanguage,
    theTvdbApiKey
  } = settings;

  return (
    <FieldSet legend={translate('Extensions')}>
      <FormGroup size={sizes.MEDIUM}>
        <FormLabel>{translate('PreferDualAudio')}</FormLabel>

        <FormInputGroup
          type={inputTypes.CHECK}
          name="preferDualAudio"
          helpText={translate('PreferDualAudioHelpText')}
          onChange={onInputChange}
          {...preferDualAudio}
        />
      </FormGroup>

      <FormGroup size={sizes.MEDIUM}>
        <FormLabel>{translate('ParseTvdbIdFromReleaseName')}</FormLabel>

        <FormInputGroup
          type={inputTypes.CHECK}
          name="parseTvdbIdFromReleaseName"
          helpText={translate('ParseTvdbIdFromReleaseNameHelpText')}
          onChange={onInputChange}
          {...parseTvdbIdFromReleaseName}
        />
      </FormGroup>

      <FormGroup size={sizes.MEDIUM}>
        <FormLabel>{translate('PreserveDownloadsForExistingEpisodes')}</FormLabel>

        <FormInputGroup
          type={inputTypes.CHECK}
          name="blockAutoImportForExistingEpisodeFiles"
          helpText={translate('PreserveDownloadsForExistingEpisodesHelpText')}
          onChange={onInputChange}
          {...blockAutoImportForExistingEpisodeFiles}
        />
      </FormGroup>

      <FormGroup size={sizes.MEDIUM}>
        <FormLabel>{translate('AnalyzeCompletedDownloadFiles')}</FormLabel>

        <FormInputGroup
          type={inputTypes.CHECK}
          name="analyzeCompletedDownloadFiles"
          helpText={translate('AnalyzeCompletedDownloadFilesHelpText')}
          onChange={onInputChange}
          {...analyzeCompletedDownloadFiles}
        />
      </FormGroup>

      <FormGroup size={sizes.MEDIUM}>
        <FormLabel>{translate('UseSeriesInfoLanguage')}</FormLabel>

        <FormInputGroup
          type={inputTypes.CHECK}
          name="useSeriesInfoLanguage"
          helpText={translate('UseSeriesInfoLanguageHelpText')}
          onChange={onInputChange}
          {...useSeriesInfoLanguage}
        />
      </FormGroup>

      <FormGroup size={sizes.MEDIUM}>
        <FormLabel>{translate('TheTvdbApiKey')}</FormLabel>

        <FormInputGroup
          type={inputTypes.PASSWORD}
          name="theTvdbApiKey"
          helpText={translate('TheTvdbApiKeyHelpText')}
          readOnly={!useSeriesInfoLanguage.value}
          onChange={onInputChange}
          {...theTvdbApiKey}
        />
      </FormGroup>
    </FieldSet>
  );
}

ExtensionsSettings.propTypes = {
  settings: PropTypes.object.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default ExtensionsSettings;
