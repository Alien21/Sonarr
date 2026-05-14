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
    parseTvdbIdFromReleaseName,
    blockAutoImportForExistingEpisodeFiles
  } = settings;

  return (
    <FieldSet legend={translate('Extensions')}>
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
    </FieldSet>
  );
}

ExtensionsSettings.propTypes = {
  settings: PropTypes.object.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default ExtensionsSettings;
